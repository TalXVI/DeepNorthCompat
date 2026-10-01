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

    // Binds one entry from the pack's config and fails if that rewrote the file.
    private static T Read<T>(string lab, string file, string section, string key, T fallback)
    {
        string path = Path.Combine(lab, "BepInEx", "config", file);
        byte[] before = File.ReadAllBytes(path);
        T value = new ConfigFile(path, false) { SaveOnConfigSet = false }.Bind(section, key, fallback).Value;
        if (!before.SequenceEqual(File.ReadAllBytes(path)))
            throw new Exception(file + " was rewritten");
        return value;
    }

    private static void Shortcut(string lab, string file, string section, string key, params KeyCode[] keys)
    {
        KeyboardShortcut value = Read(lab, file, section, key, KeyboardShortcut.Empty);
        if (value.MainKey != keys[0] || !value.Modifiers.SequenceEqual(keys.Skip(1)))
            throw new Exception(file + ": " + key + " parsed incorrectly: " + value);
    }

    private static void Key(string lab, string file, string section, string key, KeyCode expected)
    {
        if (Read(lab, file, section, key, KeyCode.None) != expected)
            throw new Exception(file + ": " + key + " parsed incorrectly");
    }

    private static void Boolean(string lab, string file, string section, string key, bool expected)
    {
        if (Read(lab, file, section, key, !expected) != expected)
            throw new Exception(file + ": " + key + " parsed incorrectly");
    }
}
