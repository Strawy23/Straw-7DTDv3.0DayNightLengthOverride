using System;
using System.Reflection;
using System.IO;
using System.Globalization;
using HarmonyLib;

public class DayNightLengthOverrideApi : IModApi
{
    public void InitMod(Mod mod)
    {
        try
        {
            DayNightLengthLog.Out("Init: XML-config DayNightLength override mode with GameStats runtime hook.");

            int overrideMinutes = DayNightLengthOverride.ReadConfigOverrideMinutes();
            if (overrideMinutes >= 1 && overrideMinutes <= 999)
            {
                DayNightLengthOverride.ConfigureOverride(overrideMinutes);
                DayNightLengthOverride.PatchRuntimeGameStatsWriters();
                DayNightLengthOverride.ApplyExplicitDayNightLength(overrideMinutes, "config override at mod init");
                DayNightLengthOverride.RegisterGameStartDoneHook();
            }
            else
            {
                DayNightLengthLog.Out("DayNightLengthOverride is 0 or invalid. Mod is inactive; vanilla value is used.");
            }
        }
        catch (Exception ex)
        {
            DayNightLengthLog.Error("Init failed: " + ex.ToString());
        }
    }
}

public static class DayNightLengthOverride
{
    private static int configuredOverrideMinutes;
    private static bool gameStartDoneHookRegistered;
    private static bool gameStartDoneApplied;
    private static bool harmonyPatchAttempted;
    private static bool applyingOverride;
    private static Harmony harmonyInstance;

    public static void ConfigureOverride(int minutes)
    {
        if (IsValidOverrideMinutes(minutes))
            configuredOverrideMinutes = minutes;
    }

    private static bool IsValidOverrideMinutes(int minutes)
    {
        return minutes >= 1 && minutes <= 999;
    }

    private static int CalculateTimeOfDayIncPerSec(int minutes)
    {
        return Math.Max(1, (int)Math.Round(2400.0 / ((double)minutes * 10.0)));
    }

    public static int ReadConfigOverrideMinutes()
    {
        try
        {
            string path = FindConfigFile();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                DayNightLengthLog.Warning("No config file found. Expected Config/DayNightLengthOverride.xml next to the mod. Mod inactive.");
                return 0;
            }

            string text = File.ReadAllText(path);
            int value = ReadNamedSettingInt(text, "DayNightLengthOverride", 0);
            if (value == 0)
            {
                DayNightLengthLog.Out("Config loaded from " + path + ": DayNightLengthOverride=0. Mod inactive; vanilla value is used.");
                return 0;
            }
            if (value >= 1 && value <= 999)
            {
                DayNightLengthLog.Out("Config loaded from " + path + ": DayNightLengthOverride=" + value);
                return value;
            }

            DayNightLengthLog.Warning("Invalid DayNightLengthOverride=" + value + "; expected 0 or a value from 1 to 999. Mod inactive.");
            return 0;
        }
        catch (Exception ex)
        {
            DayNightLengthLog.Warning("ReadConfigOverrideMinutes failed: " + ex.Message + "; mod inactive.");
            return 0;
        }
    }

    public static void PatchRuntimeGameStatsWriters()
    {
        if (harmonyPatchAttempted)
            return;

        harmonyPatchAttempted = true;

        try
        {
            Type enumStatsType = FindType("EnumGameStats");
            Type gameStatsType = FindType("GameStats");
            Type bridgeType = FindType("GameStatsBridge");
            if (enumStatsType == null || gameStatsType == null)
            {
                DayNightLengthLog.Warning("Could not find GameStats/EnumGameStats. Runtime writer hook not installed; GameStartDone verification remains active.");
                return;
            }

            harmonyInstance = new Harmony("com.straw.daynightlengthoverride");
            int patchedCount = 0;

            MethodInfo setInt = FindStaticMethod(gameStatsType, "Set", new Type[] { enumStatsType, typeof(int) });
            MethodInfo setIntPostfix = typeof(DayNightLengthOverride).GetMethod("GameStatsSetPostfix", BindingFlags.Static | BindingFlags.NonPublic);
            if (setInt != null && setIntPostfix != null)
            {
                harmonyInstance.Patch(setInt, null, new HarmonyMethod(setIntPostfix), null);
                patchedCount++;
                DayNightLengthLog.Out("Patched GameStats.Set(EnumGameStats, int) postfix for immediate DayNightLength correction.");
            }
            else
            {
                DayNightLengthLog.Warning("Could not find GameStats.Set(EnumGameStats, int); that runtime hook was not installed.");
            }

            if (bridgeType != null)
            {
                MethodInfo updateStaticFields = FindStaticMethod(bridgeType, "UpdateStaticFields", new Type[] { enumStatsType, typeof(object) });
                MethodInfo bridgePostfix = typeof(DayNightLengthOverride).GetMethod("GameStatsBridgeUpdatePostfix", BindingFlags.Static | BindingFlags.NonPublic);
                if (updateStaticFields != null && bridgePostfix != null)
                {
                    harmonyInstance.Patch(updateStaticFields, null, new HarmonyMethod(bridgePostfix), null);
                    patchedCount++;
                    DayNightLengthLog.Out("Patched GameStatsBridge.UpdateStaticFields postfix as secondary runtime correction point.");
                }
                else
                {
                    DayNightLengthLog.Warning("Could not find GameStatsBridge.UpdateStaticFields(EnumGameStats, object); secondary runtime hook was not installed.");
                }
            }
            else
            {
                DayNightLengthLog.Warning("Could not find GameStatsBridge; secondary runtime hook was not installed.");
            }

            if (patchedCount == 0)
                DayNightLengthLog.Warning("No runtime writer hooks were installed; GameStartDone verification remains active.");
        }
        catch (Exception ex)
        {
            DayNightLengthLog.Warning("Runtime writer hook setup failed: " + ex.Message + "; GameStartDone verification remains active.");
        }
    }

    private static void GameStatsSetPostfix(object[] __args)
    {
        if (applyingOverride)
            return;
        if (__args == null || __args.Length < 1)
            return;
        if (!IsRelevantGameStat(__args[0]))
            return;

        int minutes = configuredOverrideMinutes;
        if (!IsValidOverrideMinutes(minutes))
            return;

        if (!IsCurrentRuntimeStateCorrect(minutes))
            ApplyExplicitDayNightLength(minutes, "GameStats.Set runtime hook");
    }

    private static void GameStatsBridgeUpdatePostfix(object[] __args)
    {
        if (applyingOverride)
            return;
        if (__args == null || __args.Length < 1)
            return;
        if (!IsRelevantGameStat(__args[0]))
            return;

        int minutes = configuredOverrideMinutes;
        if (!IsValidOverrideMinutes(minutes))
            return;

        if (!IsCurrentRuntimeStateCorrect(minutes))
            ApplyExplicitDayNightLength(minutes, "GameStatsBridge.UpdateStaticFields runtime hook");
    }

    private static bool IsRelevantGameStat(object value)
    {
        return IsEnumValueNamed(value, "DayNightLength") || IsEnumValueNamed(value, "TimeOfDayIncPerSec");
    }

    private static bool IsCurrentRuntimeStateCorrect(int minutes)
    {
        int currentMinutes = GetGameStatInt("DayNightLength", -9999);
        int currentInc = GetGameStatInt("TimeOfDayIncPerSec", -9999);
        int wantedInc = CalculateTimeOfDayIncPerSec(minutes);
        return currentMinutes == minutes && currentInc == wantedInc;
    }

    public static void ApplyExplicitDayNightLength(int minutes, string reason)
    {
        if (!IsValidOverrideMinutes(minutes))
        {
            DayNightLengthLog.Warning("ApplyExplicitDayNightLength ignored invalid value: " + minutes + "; expected 1 to 999.");
            return;
        }

        int incPerSec = CalculateTimeOfDayIncPerSec(minutes);
        bool oldApplying = applyingOverride;
        applyingOverride = true;
        bool prefOk = false;
        bool statOk = false;
        bool incOk = false;
        try
        {
            prefOk = SetGamePrefInt("DayNightLength", minutes);
            statOk = SetGameStatInt("DayNightLength", minutes);
            incOk = SetGameStatInt("TimeOfDayIncPerSec", incPerSec);
        }
        finally
        {
            applyingOverride = oldApplying;
        }

        DayNightLengthLog.Out("Applied DayNightLength override from " + reason + ": " + minutes +
                           " minute(s) (GamePref set=" + prefOk + "; GameStat set=" + statOk +
                           "; TimeOfDayIncPerSec set=" + incOk + "; TimeOfDayIncPerSec=" + incPerSec + ").");
    }

    public static void RegisterGameStartDoneHook()
    {
        if (gameStartDoneHookRegistered)
            return;

        gameStartDoneHookRegistered = true;

        try
        {
            ModEvents.GameStartDone.RegisterHandler(OnGameStartDone);
            DayNightLengthLog.Out("Registered GameStartDone final verification hook.");
        }
        catch (Exception ex)
        {
            DayNightLengthLog.Warning("Could not register GameStartDone final verification hook: " + ex.Message);
        }
    }

    private static void OnGameStartDone(ref ModEvents.SGameStartDoneData data)
    {
        if (gameStartDoneApplied)
            return;

        gameStartDoneApplied = true;

        int minutes = configuredOverrideMinutes;
        if (!IsValidOverrideMinutes(minutes))
            return;

        int prefValue = GetGamePrefInt("DayNightLength", -9999);
        int statValue = GetGameStatInt("DayNightLength", -9999);
        int incValue = GetGameStatInt("TimeOfDayIncPerSec", -9999);
        int wantedInc = CalculateTimeOfDayIncPerSec(minutes);
        if (prefValue == minutes && statValue == minutes && incValue == wantedInc)
        {
            DayNightLengthLog.Out("GameStartDone final verification: DayNightLength already correct at " + minutes + " minute(s), TimeOfDayIncPerSec=" + wantedInc + ".");
            return;
        }

        ApplyExplicitDayNightLength(minutes, "GameStartDone final verification hook");
    }

    private static string FindConfigFile()
    {
        string dllPath = null;
        try { dllPath = Assembly.GetExecutingAssembly().Location; } catch { }
        if (string.IsNullOrEmpty(dllPath))
            return null;

        string dllDir = Path.GetDirectoryName(dllPath);
        if (string.IsNullOrEmpty(dllDir))
            return null;

        string path = Path.Combine(dllDir, "Config", "DayNightLengthOverride.xml");
        return File.Exists(path) ? path : null;
    }

    private static int ReadNamedSettingInt(string text, string settingName, int defaultValue)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(settingName))
            return defaultValue;

        int nameIx = text.IndexOf("name=\"" + settingName + "\"", StringComparison.OrdinalIgnoreCase);
        if (nameIx < 0)
            nameIx = text.IndexOf("name='" + settingName + "'", StringComparison.OrdinalIgnoreCase);
        if (nameIx < 0)
            return defaultValue;

        int endIx = text.IndexOf('>', nameIx);
        if (endIx < 0)
            endIx = Math.Min(text.Length, nameIx + 300);

        string segment = text.Substring(nameIx, endIx - nameIx);
        string valueText = ReadAttribute(segment, "value");
        int parsed;
        if (int.TryParse(valueText, out parsed))
            return parsed;
        return defaultValue;
    }

    private static string ReadAttribute(string text, string attrName)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(attrName))
            return null;

        string marker = attrName + "=\"";
        int ix = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        char quote = '\"';
        if (ix < 0)
        {
            marker = attrName + "='";
            ix = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            quote = '\'';
        }
        if (ix < 0)
            return null;

        int start = ix + marker.Length;
        int end = text.IndexOf(quote, start);
        if (end < 0)
            return null;
        return text.Substring(start, end - start).Trim();
    }

    private static bool IsEnumValueNamed(object value, string expectedName)
    {
        if (value == null || string.IsNullOrEmpty(expectedName))
            return false;
        try
        {
            return string.Equals(value.ToString(), expectedName, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static MethodInfo FindStaticMethod(Type type, string methodName, Type[] parameterTypes)
    {
        if (type == null || string.IsNullOrEmpty(methodName))
            return null;

        MethodInfo[] methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < methods.Length; i++)
        {
            MethodInfo method = methods[i];
            if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
                continue;

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != parameterTypes.Length)
                continue;

            bool matches = true;
            for (int p = 0; p < parameters.Length; p++)
            {
                if (parameters[p].ParameterType != parameterTypes[p])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return method;
        }

        return null;
    }

    private static int GetGamePrefInt(string prefName, int defaultValue)
    {
        try
        {
            Type prefsType = FindType("GamePrefs");
            Type enumType = FindType("EnumGamePrefs");
            if (prefsType == null || enumType == null)
                return defaultValue;
            object key = Enum.Parse(enumType, prefName);
            MethodInfo getInt = prefsType.GetMethod("GetInt", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { enumType }, null);
            if (getInt == null)
                return defaultValue;
            object value = getInt.Invoke(null, new object[] { key });
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static int GetGameStatInt(string statName, int defaultValue)
    {
        try
        {
            Type statsType = FindType("GameStats");
            Type enumType = FindType("EnumGameStats");
            if (statsType == null || enumType == null)
                return defaultValue;
            object key = Enum.Parse(enumType, statName);
            MethodInfo getInt = statsType.GetMethod("GetInt", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { enumType }, null);
            if (getInt == null)
                return defaultValue;
            object value = getInt.Invoke(null, new object[] { key });
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    private static bool SetGamePrefInt(string prefName, int value)
    {
        try
        {
            Type prefsType = FindType("GamePrefs");
            Type enumType = FindType("EnumGamePrefs");
            if (prefsType == null || enumType == null)
                return false;
            object key = Enum.Parse(enumType, prefName);
            MethodInfo set = prefsType.GetMethod("Set", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { enumType, typeof(int) }, null);
            if (set == null)
                return false;
            set.Invoke(null, new object[] { key, value });
            return true;
        }
        catch (Exception ex)
        {
            DayNightLengthLog.Warning("SetGamePrefInt(" + prefName + ") failed: " + ex.Message);
            return false;
        }
    }

    private static bool SetGameStatInt(string statName, int value)
    {
        try
        {
            Type statsType = FindType("GameStats");
            Type enumType = FindType("EnumGameStats");
            if (statsType == null || enumType == null)
                return false;
            object key = Enum.Parse(enumType, statName);
            MethodInfo set = statsType.GetMethod("Set", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { enumType, typeof(int) }, null);
            if (set == null)
                return false;
            set.Invoke(null, new object[] { key, value });
            return true;
        }
        catch (Exception ex)
        {
            DayNightLengthLog.Warning("SetGameStatInt(" + statName + ") failed: " + ex.Message);
            return false;
        }
    }

    internal static Type FindType(string fullName)
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < assemblies.Length; i++)
        {
            Type type = assemblies[i].GetType(fullName, false);
            if (type != null)
                return type;
        }
        return Type.GetType(fullName, false);
    }
}

internal static class DayNightLengthLog
{
    private const string Prefix = "[DayNightLengthOverride] ";

    public static void Out(string message)
    {
        Write("Out", Prefix + message);
    }

    public static void Warning(string message)
    {
        Write("Warning", Prefix + message);
    }

    public static void Error(string message)
    {
        Write("Error", Prefix + message);
    }

    private static void Write(string logMethod, string message)
    {
        try
        {
            Type logType = DayNightLengthOverride.FindType("Log");
            if (logType != null)
            {
                MethodInfo method = logType.GetMethod(logMethod, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(string) }, null);
                if (method != null)
                {
                    method.Invoke(null, new object[] { message });
                    return;
                }
            }
        }
        catch { }

        try { Console.WriteLine(message); } catch { }
    }
}
