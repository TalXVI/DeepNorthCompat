using System;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

internal static class ConfigValidation
{
    internal static void Run(string lab)
    {
        Shortcut(lab, "Azumatt.AzuCraftyBoxes.cfg", "3 - Keys", "Container Toggle Key", KeyCode.J);
        Shortcut(lab, "shudnal.MyLittleUI.cfg", "Chat - Item links", "Link item modifier", KeyCode.RightShift);
        Key(lab, "Turbero.ForsakenPowersPlusRemastered.cfg", "2 - General", "ForsakenPowerHotkey", KeyCode.F4);
        Key(lab, "Turbero.ForsakenPowersPlusRemastered.cfg", "2 - General", "ResetPowerHotkey", KeyCode.None);
        Shortcut(lab, "xyz.alcan.comfortcalc.cfg", "5. Comfort Pieces List", "Toggle Visibility", KeyCode.None);
        Shortcut(lab, "Azumatt.Recycle_N_Reclaim.cfg", "2 - Inventory Recycle", "DiscardHotkey(s)", KeyCode.None);
        Shortcut(lab, "Azumatt.HearthBelow.cfg", "4 - Dig Controls", "Toggle Dig Preview", KeyCode.B, KeyCode.LeftShift);
        Shortcut(lab, "Azumatt.HearthBelow.cfg", "4 - Dig Controls", "Cycle Dig Mode", KeyCode.PageDown);
        Shortcut(lab, "Azumatt.AzuAntiArthriticCrafting.cfg", "4 - Keys", "Toggle RecipeUI", KeyCode.PageUp, KeyCode.LeftShift);
        Shortcut(lab, "_shudnal.ConfigurationManager.cfg", "General", "Reset position and size", KeyCode.F1, KeyCode.LeftAlt);
        Boolean(lab, "ZenDragon.Zen.ModLib.cfg", "BugFix", "Apply Player Key", false);
        Boolean(lab, "ZenDragon.Zen.ModLib.cfg", "General", "Allow Achievements With DevCommands", false);
        Boolean(lab, "MK_BetterUI.cfg", "2 - Character Inventory", "showItemStars", false);
        Shortcut(lab, "Azumatt.HearthBelow.cfg", "4 - Dig Controls", "Dig Controls Modifier", KeyCode.LeftShift);
    }

    private static ConfigFile Open(string lab, string file)
    {
        return new ConfigFile(Path.Combine(lab, "BepInEx", "config", file), false) { SaveOnConfigSet = false };
    }

    private static void Shortcut(string lab, string file, string section, string key, params KeyCode[] keys)
    {
        byte[] before = File.ReadAllBytes(Path.Combine(lab, "BepInEx", "config", file));
        KeyboardShortcut value = Open(lab, file).Bind(section, key, KeyboardShortcut.Empty).Value;
        if (value.MainKey != keys[0] || !value.Modifiers.SequenceEqual(keys.Skip(1)))
            throw new Exception(file + ": " + key + " parsed incorrectly: " + value);
        if (!before.SequenceEqual(File.ReadAllBytes(Path.Combine(lab, "BepInEx", "config", file))))
            throw new Exception(file + " was rewritten");
    }

    private static void Key(string lab, string file, string section, string key, KeyCode expected)
    {
        if (Open(lab, file).Bind(section, key, KeyCode.None).Value != expected)
            throw new Exception(file + ": " + key + " parsed incorrectly");
    }

    private static void Boolean(string lab, string file, string section, string key, bool expected)
    {
        if (Open(lab, file).Bind(section, key, !expected).Value != expected)
            throw new Exception(file + ": " + key + " parsed incorrectly");
    }
}
