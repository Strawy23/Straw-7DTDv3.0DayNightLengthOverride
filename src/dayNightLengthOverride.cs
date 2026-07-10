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
            DayNightLengthLog.Out("Init: XML-config DayNightLength override mode with precise runtime time-scale hook.");

            int overrideMinutes = DayNightLengthOverride.ReadConfigOverrideMinutes();
            if (overrideMinutes >= 1 && overrideMinutes <= 2400)
            {
                DayNightLengthOverride.ConfigureOverride(overrideMinutes);
                DayNightLengthOverride.PatchRuntimeGameStatsWriters();
                DayNightLengthOverride.PatchPreciseTimeScaleHooks();
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
    private static bool precisePatchAttempted;
    private static bool precisePatchInstalled;
    private static bool preciseGetIntInstalled;
    private static bool inTimeOfDayUpdate;
    private static bool suppressPreciseGetInt;
    private static bool runtimeCorrectionLogWritten;
    private static double preciseCarrySeconds;
    private static int preciseFrameIncPerSec;
    private static bool applyingOverride;
    private static Harmony harmonyInstance;
    private static MethodInfo unityDeltaTimeGetter;

    public static void ConfigureOverride(int minutes)
    {
        if (IsValidOverrideMinutes(minutes))
            configuredOverrideMinutes = minutes;
    }

    private static bool IsValidOverrideMinutes(int minutes)
    {
        return minutes >= 1 && minutes <= 2400;
    }

    private static int CalculateTimeOfDayIncPerSec(int minutes)
    {
        return Math.Max(1, (int)Math.Round(2400.0 / (double)minutes));
    }

    private static double CalculateExactTimeOfDayIncPerSec(int minutes)
    {
        if (minutes <= 0)
            return 0.0;
        return 2400.0 / (double)minutes;
    }

    private static bool NeedsPreciseTimeScale(int minutes)
    {
        double exact = CalculateExactTimeOfDayIncPerSec(minutes);
        return Math.Abs(exact - Math.Round(exact)) > 0.000001;
    }

    public static int ReadConfigOverrideMinutes()
    {
        try
        {
            string path = FindConfigFile();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                DayNightLengthLog.Warning("No config file found. Expected Config/DayNightLengthOverride.xml in the mod folder. Mod inactive.");
                return 0;
            }

            string text = File.ReadAllText(path);
            int value = ReadNamedSettingInt(text, "DayNightLengthOverride", 0);
            if (value == 0)
            {
                DayNightLengthLog.Out("Config loaded from " + path + ": DayNightLengthOverride=0. Mod inactive; vanilla value is used.");
                return 0;
            }
            if (value >= 1 && value <= 2400)
            {
                DayNightLengthLog.Out("Config loaded from " + path + ": DayNightLengthOverride=" + value);
                return value;
            }

            DayNightLengthLog.Warning("Invalid DayNightLengthOverride=" + value + "; expected 0 or a value from 1 to 2400. Mod inactive.");
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

    public static void PatchPreciseTimeScaleHooks()
    {
        if (precisePatchAttempted)
            return;

        precisePatchAttempted = true;

        int minutes = configuredOverrideMinutes;
        if (!IsValidOverrideMinutes(minutes))
            return;

        try
        {
            Type enumStatsType = FindType("EnumGameStats");
            Type gameStatsType = FindType("GameStats");
            if (enumStatsType != null && gameStatsType != null)
            {
                if (harmonyInstance == null)
                    harmonyInstance = new Harmony("com.straw.daynightlengthoverride");

                MethodInfo getInt = FindStaticMethod(gameStatsType, "GetInt", new Type[] { enumStatsType });
                MethodInfo getIntPrefix = typeof(DayNightLengthOverride).GetMethod("GameStatsGetIntPrefix", BindingFlags.Static | BindingFlags.NonPublic);
                if (getInt != null && getIntPrefix != null)
                {
                    harmonyInstance.Patch(getInt, new HarmonyMethod(getIntPrefix), null, null);
                    preciseGetIntInstalled = true;
                    DayNightLengthLog.Out("Patched GameStats.GetInt(EnumGameStats) prefix for precise TimeOfDayIncPerSec reads.");
                }
                else
                {
                    DayNightLengthLog.Warning("Could not find GameStats.GetInt(EnumGameStats); precise time-scale read hook was not installed.");
                }
            }
            else
            {
                DayNightLengthLog.Warning("Could not find GameStats/EnumGameStats; precise time-scale read hook was not installed.");
            }

            Type gameManagerType = FindType("GameManager");
            if (gameManagerType != null)
            {
                MethodInfo updateMethod = FindMethodByName(gameManagerType, "updateTimeOfDay");
                MethodInfo updatePrefix = typeof(DayNightLengthOverride).GetMethod("GameManagerUpdateTimeOfDayPrefix", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo updatePostfix = typeof(DayNightLengthOverride).GetMethod("GameManagerUpdateTimeOfDayPostfix", BindingFlags.Static | BindingFlags.NonPublic);
                if (updateMethod != null && updatePrefix != null && updatePostfix != null)
                {
                    if (harmonyInstance == null)
                        harmonyInstance = new Harmony("com.straw.daynightlengthoverride");
                    harmonyInstance.Patch(updateMethod, new HarmonyMethod(updatePrefix), new HarmonyMethod(updatePostfix), null);
                    precisePatchInstalled = true;
                    DayNightLengthLog.Out("Patched GameManager.updateTimeOfDay for precise day lengths above vanilla integer buckets.");
                }
                else
                {
                    DayNightLengthLog.Warning("Could not find GameManager.updateTimeOfDay; precise time-scale update hook was not installed.");
                }
            }
            else
            {
                DayNightLengthLog.Warning("Could not find GameManager; precise time-scale update hook was not installed.");
            }
        }
        catch (Exception ex)
        {
            DayNightLengthLog.Warning("Precise time-scale hook setup failed: " + ex.Message + "; falling back to integer TimeOfDayIncPerSec.");
        }
    }

    private static void GameManagerUpdateTimeOfDayPrefix()
    {
        int minutes = configuredOverrideMinutes;
        if (!IsValidOverrideMinutes(minutes) || !preciseGetIntInstalled)
            return;

        preciseFrameIncPerSec = CalculatePreciseFrameIncPerSec(minutes);
        inTimeOfDayUpdate = true;
    }

    private static void GameManagerUpdateTimeOfDayPostfix()
    {
        inTimeOfDayUpdate = false;
    }

    private static bool GameStatsGetIntPrefix(object[] __args, ref int __result)
    {
        if (suppressPreciseGetInt || applyingOverride || !inTimeOfDayUpdate)
            return true;
        if (__args == null || __args.Length < 1)
            return true;
        if (!IsEnumValueNamed(__args[0], "TimeOfDayIncPerSec"))
            return true;

        __result = preciseFrameIncPerSec;
        return false;
    }

    private static int CalculatePreciseFrameIncPerSec(int minutes)
    {
        double exact = CalculateExactTimeOfDayIncPerSec(minutes);
        if (exact <= 0.0)
            return CalculateTimeOfDayIncPerSec(minutes);

        int baseInc = (int)Math.Floor(exact);
        double fraction = exact - (double)baseInc;
        if (fraction <= 0.000001)
            return Math.Max(1, baseInc);

        double delta = GetUnityDeltaTime();
        if (delta <= 0.000001 || delta > 1.0)
            delta = 0.02;

        preciseCarrySeconds += fraction * delta;
        int extra = 0;
        while (preciseCarrySeconds + 0.0000001 >= delta)
        {
            extra++;
            preciseCarrySeconds -= delta;
            if (extra > 4)
                break;
        }

        int result = baseInc + extra;
        if (result < 1)
            result = 1;
        return result;
    }

    private static double GetUnityDeltaTime()
    {
        try
        {
            if (unityDeltaTimeGetter == null)
            {
                Type timeType = FindType("UnityEngine.Time");
                if (timeType != null)
                {
                    PropertyInfo prop = timeType.GetProperty("deltaTime", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (prop != null)
                        unityDeltaTimeGetter = prop.GetGetMethod(true);
                }
            }

            if (unityDeltaTimeGetter != null)
            {
                object value = unityDeltaTimeGetter.Invoke(null, null);
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
        }
        catch { }

        return 0.02;
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
        if (currentMinutes != minutes)
            return false;

        if (precisePatchInstalled && NeedsPreciseTimeScale(minutes))
            return true;

        int currentInc = GetGameStatInt("TimeOfDayIncPerSec", -9999);
        int wantedInc = CalculateTimeOfDayIncPerSec(minutes);
        return currentInc == wantedInc;
    }

    public static void ApplyExplicitDayNightLength(int minutes, string reason)
    {
        if (!IsValidOverrideMinutes(minutes))
        {
            DayNightLengthLog.Warning("ApplyExplicitDayNightLength ignored invalid value: " + minutes + "; expected 1 to 2400.");
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

        LogApplyResult(reason, minutes, prefOk, statOk, incOk, incPerSec);
    }

    private static void LogApplyResult(string reason, int minutes, bool prefOk, bool statOk, bool incOk, int incPerSec)
    {
        bool runtime = !string.IsNullOrEmpty(reason) && reason.IndexOf("runtime hook", StringComparison.OrdinalIgnoreCase) >= 0;
        string preciseText = precisePatchInstalled && NeedsPreciseTimeScale(minutes) ? "; precise time-scale hook active" : "";
        string message = "Applied DayNightLength override from " + reason + ": " + minutes +
                         " minute(s) (GamePref set=" + prefOk + "; GameStat set=" + statOk +
                         "; TimeOfDayIncPerSec set=" + incOk + "; fallback TimeOfDayIncPerSec=" + incPerSec + preciseText + ").";

        if (runtime)
        {
            if (runtimeCorrectionLogWritten)
                return;
            runtimeCorrectionLogWritten = true;
            DayNightLengthLog.Out(message);
            return;
        }

        DayNightLengthLog.Out(message);
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
        int wantedInc = CalculateTimeOfDayIncPerSec(minutes);
        if (prefValue == minutes && statValue == minutes)
        {
            if (precisePatchInstalled && NeedsPreciseTimeScale(minutes))
            {
                DayNightLengthLog.Out("GameStartDone final verification: DayNightLength already correct at " + minutes + " minute(s); precise time-scale hook is active with fallback TimeOfDayIncPerSec=" + wantedInc + ".");
                return;
            }

            int incValue = GetGameStatInt("TimeOfDayIncPerSec", -9999);
            if (incValue == wantedInc)
            {
                DayNightLengthLog.Out("GameStartDone final verification: DayNightLength already correct at " + minutes + " minute(s), TimeOfDayIncPerSec=" + wantedInc + ".");
                return;
            }
        }

        ApplyExplicitDayNightLength(minutes, "GameStartDone final verification hook");
    }

    private static string FindConfigFile()
    {
        string fileName = "DayNightLengthOverride.xml";
        string dllPath = null;
        try { dllPath = Assembly.GetExecutingAssembly().Location; } catch { }

        string dllDir = null;
        if (!string.IsNullOrEmpty(dllPath))
            dllDir = Path.GetDirectoryName(dllPath);

        string[] candidates = new string[64];
        int count = 0;

        if (!string.IsNullOrEmpty(dllDir))
        {
            count = AddConfigCandidate(candidates, count, Path.Combine(dllDir, "Config", fileName));
            count = AddConfigCandidate(candidates, count, Path.Combine(dllDir, fileName));

            DirectoryInfo parentInfo = null;
            try { parentInfo = Directory.GetParent(dllDir); } catch { }
            string parent = parentInfo != null ? parentInfo.FullName : null;
            if (!string.IsNullOrEmpty(parent))
            {
                count = AddConfigCandidate(candidates, count, Path.Combine(parent, "Config", fileName));
                count = AddConfigCandidate(candidates, count, Path.Combine(parent, fileName));
            }
        }

        string baseDir = null;
        try { baseDir = AppDomain.CurrentDomain.BaseDirectory; } catch { }
        if (!string.IsNullOrEmpty(baseDir))
        {
            string modsDir = Path.Combine(baseDir, "Mods");
            if (Directory.Exists(modsDir))
            {
            candidates[count++] = Path.Combine(modsDir, "Straw-DayNightLengthOverride", "Config", fileName);
            candidates[count++] = Path.Combine(modsDir, "Straw-DayNightLengthOverride1.3", "Config", fileName);
            candidates[count++] = Path.Combine(modsDir, "Straw-DayNightLengthOverride1.2-test", "Config", fileName);
            candidates[count++] = Path.Combine(modsDir, "Straw-DayNightLengthOverride1.1-test", "Config", fileName);
                try
                {
                    string[] matchingDirs = Directory.GetDirectories(modsDir, "*DayNightLengthOverride*", SearchOption.TopDirectoryOnly);
                    for (int i = 0; i < matchingDirs.Length; i++)
                        count = AddConfigCandidate(candidates, count, Path.Combine(matchingDirs[i], "Config", fileName));
                }
                catch { }
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (!string.IsNullOrEmpty(candidates[i]) && File.Exists(candidates[i]))
                return candidates[i];
        }

        return null;
    }

    private static int AddConfigCandidate(string[] candidates, int count, string path)
    {
        if (string.IsNullOrEmpty(path) || candidates == null || count >= candidates.Length)
            return count;

        for (int i = 0; i < count; i++)
        {
            if (string.Equals(candidates[i], path, StringComparison.OrdinalIgnoreCase))
                return count;
        }

        candidates[count] = path;
        return count + 1;
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
        char quote = '"';
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

    private static MethodInfo FindMethodByName(Type type, string methodName)
    {
        if (type == null || string.IsNullOrEmpty(methodName))
            return null;

        MethodInfo[] methods = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < methods.Length; i++)
        {
            if (string.Equals(methods[i].Name, methodName, StringComparison.Ordinal))
                return methods[i];
        }

        return null;
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
        bool oldSuppress = suppressPreciseGetInt;
        suppressPreciseGetInt = true;
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
        finally
        {
            suppressPreciseGetInt = oldSuppress;
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
