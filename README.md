# Straw Day/Night Length Override

A small 7 Days to Die V3.0 dedicated-server mod that lets you force the `DayNightLength` / `24 Hour Cycle` setting to any value from 1 to 999 minutes through an XML config file.

## What it does

The vanilla sandbox menu only exposes selected 24 hour cycle values. This mod reads `Config/DayNightLengthOverride.xml` and applies the configured value to `GamePref.DayNightLength`, `GameStat.DayNightLength`, and the derived `GameStat.TimeOfDayIncPerSec` during startup.

The runtime hook corrects the value again if the game rewrites it during startup. A final `GameStartDone` verification confirms both the configured day length and the derived time increment before the server is ready.

## Config

Edit:

```xml
Config/DayNightLengthOverride.xml
```

Default:

```xml
<setting name="DayNightLengthOverride" value="0" />
```

Values:

```text
0     = disabled; use the game's normal configured value
1-999 = force that exact 24 hour cycle length in minutes
```

Examples:

```xml
<setting name="DayNightLengthOverride" value="80" />
```

```xml
<setting name="DayNightLengthOverride" value="240" />
```

## EAC note

Dedicated server use is compatible with EAC. 
Single-player and player-hosted use requires EAC disabled.
Clients do not need this mod in a multiplayer scenario.

## Console check

Use server console, telnet, or RCON:

```text
getgamepref DayNightLength
getgamestat DayNightLength
```

Both should show the configured value once the mod is active. The log should also show `TimeOfDayIncPerSec` being set to `round(2400 / (DayNightLength * 10))`, for example `48` for a 5 minute day.
