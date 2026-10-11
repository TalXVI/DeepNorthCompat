using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace DeepNorthCompat
{
    // The native body and map versions remain unchanged. Readers without DNC ignore
    // this trailer; category identity survives runtime slot reassignment for DNC readers.
    internal sealed class BetterMapMapData
    {
        private const int Magic = 0x444E4D50, MetadataVersion = 2;
        internal sealed class Identity
        {
            internal int Category;
            internal bool Automatic, Legacy;
            internal Identity(int category, bool automatic) : this(category, automatic, false) { }
            internal Identity(int category, bool automatic, bool legacy) { Category = category; Automatic = automatic; Legacy = legacy; }
        }
        internal sealed class Row
        {
            internal string Name = "";
            internal Vector3 Position;
            internal int Type, TypeOffset;
            internal long Owner;
            internal bool Checked;
            internal string Author = "";
            internal Identity Identity = new Identity(-1, false);
            // The author is passed in because formatting it allocates; compare the cheap fields first.
            internal bool Matches(Minimap.PinData pin, string author, int slots) => pin.m_pos == Position
                && ((int)pin.m_type == Type || (Type < 0 || Type >= slots) && pin.m_type == Minimap.PinType.Icon3)
                && pin.m_ownerID == Owner && pin.m_checked == Checked && pin.m_name == Name && author == Author;
        }
        internal readonly List<Row> Rows = new List<Row>();
        // The body is the decompressed local payload; shared data is parsed in place.
        private readonly byte[] input, body;
        private readonly int version, nativeLength, tailStart;
        private readonly bool shared;
        internal bool HasMetadata { get; private set; }

        internal BetterMapMapData(byte[] input, bool shared, bool ignoreMetadata = false)
        {
            this.input = input; this.shared = shared; nativeLength = input.Length;
            Identity[]? identities = ignoreMetadata ? null : ReadMetadata(input, out nativeLength);
            HasMetadata = identities != null;
            var outer = new ZPackage(input); version = outer.ReadInt();
            if (version < 1 || version > (shared ? 3 : 8)) throw new NotSupportedException("Unsupported native map version " + version);
            var package = shared ? outer : version >= 7 ? outer.ReadCompressedPackage() : new ZPackage(outer.ReadByteArray(nativeLength - 4));
            body = shared ? input : package.GetArray();
            tailStart = outer.GetPos();
            int limit = shared ? nativeLength : package.Size();
            int size = package.ReadInt();
            int cells = shared ? size : checked(size * size);
            int exploreBytes = checked(cells * (!shared && version >= 5 ? 2 : 1));
            int next = checked(package.GetPos() + exploreBytes);
            if (cells < 0 || next > limit) throw new InvalidDataException("Map exploration length exceeds its body.");
            package.SetPos(next);
            if (version >= 2)
            {
                int count = package.ReadInt();
                if (count < 0 || count > (limit - package.GetPos()) / 17) throw new InvalidDataException("Invalid native pin count.");
                for (int i = 0; i < count; i++)
                {
                    var row = new Row();
                    if (shared) row.Owner = package.ReadLong();
                    row.Name = package.ReadString(); row.Position = package.ReadVector3();
                    row.TypeOffset = package.GetPos(); row.Type = package.ReadInt();
                    if (shared || version >= 3) row.Checked = package.ReadBool();
                    if (!shared && version >= 6) row.Owner = package.ReadLong();
                    if (shared && version >= 3 || !shared && version >= 8) row.Author = package.ReadString();
                    Rows.Add(row);
                }
            }
            if (identities != null)
            {
                if (identities.Length != Rows.Count) throw new InvalidDataException("BetterMap metadata pin count disagrees with the native body.");
                for (int i = 0; i < Rows.Count; i++) Rows[i].Identity = identities[i];
            }
        }

        // Returns the input itself when no type changes, sparing a copy and a recompression of the map.
        internal byte[] Translate(Func<int, Minimap.PinType?> typeFor)
        {
            byte[]? changed = null;
            foreach (Row row in Rows)
            {
                if (row.Identity.Category < 0) continue;
                Minimap.PinType? allocated = typeFor(row.Identity.Category);
                if (!allocated.HasValue) continue;
                int type = (int)allocated.Value;
                if (type == row.Type) continue;
                if (changed == null) changed = Slice(body, 0, shared ? nativeLength : body.Length);
                Array.Copy(BitConverter.GetBytes(type), 0, changed, row.TypeOffset, 4);
                row.Type = type;
            }
            return changed == null ? input : NativeWithMetadata(changed);
        }
        private byte[] NativeWithMetadata(byte[] payload)
        {
            byte[] native;
            if (shared) native = payload;
            else
            {
                var wrapper = new ZPackage(); wrapper.Write(version);
                if (version >= 7)
                {
                    wrapper.WriteCompressed(new ZPackage(payload));
                    native = Join(wrapper.GetArray(), Slice(input, tailStart, nativeLength - tailStart));
                }
                else native = Join(wrapper.GetArray(), payload);
            }
            return HasMetadata ? Append(native, Rows.Select(r => r.Identity).ToArray()) : native;
        }
        internal static byte[] Append(byte[] native, Identity[] identities)
        {
            var trailer = new ZPackage(); trailer.Write(MetadataVersion); trailer.Write(identities.Length);
            foreach (Identity identity in identities)
            {
                if (identity.Category != -1 && !BetterMapAccess.SerializableCategory(identity.Category)
                    || identity.Automatic && identity.Category < 0 || identity.Legacy && (identity.Category >= 0 || identity.Automatic))
                    throw new InvalidDataException("Invalid category metadata.");
                trailer.Write(identity.Category); trailer.Write(identity.Automatic); trailer.Write(identity.Legacy);
            }
            byte[] bytes = trailer.GetArray(); var footer = new ZPackage(); footer.Write(bytes.Length); footer.Write(Magic);
            return Join(native, bytes, footer.GetArray());
        }
        private static Identity[]? ReadMetadata(byte[] input, out int nativeLength)
        {
            nativeLength = input.Length;
            if (input.Length < 8 || BitConverter.ToInt32(input, input.Length - 4) != Magic) return null;
            int length = BitConverter.ToInt32(input, input.Length - 8);
            if (length < 8 || length > input.Length - 12) throw new InvalidDataException("Invalid BetterMap metadata length.");
            var package = new ZPackage(Slice(input, input.Length - 8 - length, length));
            int metadataVersion = package.ReadInt();
            if (metadataVersion != 1 && metadataVersion != MetadataVersion) throw new NotSupportedException("Unsupported BetterMap pin metadata version.");
            int count = package.ReadInt();
            if (count < 0 || 8L + (metadataVersion == 1 ? 5L : 6L) * count != length) throw new InvalidDataException("Invalid BetterMap metadata count.");
            var identities = new Identity[count];
            for (int i = 0; i < count; i++)
            {
                int category = package.ReadInt(); bool automatic = package.ReadBool();
                bool legacy = metadataVersion >= 2 && package.ReadBool();
                if (category != -1 && !BetterMapAccess.SerializableCategory(category) || automatic && category < 0
                    || legacy && (category >= 0 || automatic))
                    throw new InvalidDataException("Invalid BetterMap category metadata.");
                identities[i] = new Identity(category, automatic, legacy);
            }
            nativeLength = input.Length - 8 - length;
            return identities;
        }
        private static byte[] Slice(byte[] source, int start, int length)
        {
            var result = new byte[length]; Buffer.BlockCopy(source, start, result, 0, length); return result;
        }
        private static byte[] Join(params byte[][] parts)
        {
            var result = new byte[parts.Sum(p => p.Length)]; int offset = 0;
            foreach (byte[] part in parts) { Array.Copy(part, 0, result, offset, part.Length); offset += part.Length; }
            return result;
        }
    }
}
