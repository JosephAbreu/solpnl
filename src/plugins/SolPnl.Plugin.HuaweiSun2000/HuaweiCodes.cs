namespace SolPnl.Plugin.HuaweiSun2000;

/// <summary>
/// Huawei SUN2000 alarms and device states, from huawei-solar-lib. Alarms are reported by their Huawei alarm ID
/// (e.g. 2032 "Grid loss"), the number the FusionSolar app and Huawei's manuals use.
/// </summary>
public static class HuaweiCodes
{
    /// <summary>Alarm register 32008, 32009, 32010: bit n → alarm ID and name.</summary>
    private static readonly (int Id, string Name)[][] AlarmBits =
    [
        [
            (2001, "String input voltage too high"), (2002, "DC arc fault"), (2011, "String connected in reverse"),
            (2012, "String current backfeed"), (2013, "Abnormal string power"), (2021, "Arc fault (AFCI) self-check failed"),
            (2031, "Phase wire short-circuited to earth"), (2032, "Grid loss"), (2033, "Grid voltage too low"),
            (2034, "Grid voltage too high"), (2035, "Grid voltage imbalance"), (2036, "Grid frequency too high"),
            (2037, "Grid frequency too low"), (2038, "Unstable grid frequency"), (2039, "Output current too high"),
            (2040, "Output DC component too high"),
        ],
        [
            (2051, "Abnormal residual current"), (2061, "Abnormal grounding"), (2062, "Low insulation resistance"),
            (2063, "Too hot"), (2064, "Device fault"), (2065, "Upgrade failed or version mismatch"),
            (2066, "License expired"), (61440, "Faulty monitoring unit"), (2067, "Faulty power collector"),
            (2068, "Battery abnormal"), (2070, "Active islanding"), (2071, "Passive islanding"),
            (2072, "Transient AC overvoltage"), (2075, "Peripheral port short circuit"), (2077, "Off-grid output overload"),
            (2080, "Abnormal PV module configuration"),
        ],
        [
            (2081, "Optimizer fault"), (2085, "Built-in PID abnormal"), (2014, "String voltage to ground too high"),
            (2086, "External fan abnormal"), (2069, "Battery connected in reverse"),
            (2082, "On-grid/off-grid controller abnormal"), (2015, "PV string lost"), (2087, "Internal fan abnormal"),
            (2088, "DC protection unit abnormal"), (2089, "EL unit abnormal"),
            (2090, "Active power adjustment instruction abnormal"), (2091, "Reactive power adjustment instruction abnormal"),
            (2092, "CT wiring abnormal"), (2003, "DC arc fault (clear manually)"), (2093, "DC switch abnormal"),
            (2094, "Battery's allowed discharge capacity low"),
        ],
    ];

    private static readonly Dictionary<int, string> AlarmNames =
        AlarmBits.SelectMany(word => word).ToDictionary(a => a.Id, a => a.Name);

    public static string? DescribeAlarm(int id) => AlarmNames.GetValueOrDefault(id);

    /// <summary>Alarm IDs of the set bits, in register and bit order.</summary>
    public static List<int> ActiveAlarms(params double[] words)
    {
        var ids = new List<int>();
        for (var w = 0; w < AlarmBits.Length && w < words.Length; w++)
        {
            var bits = (int)words[w];
            for (var bit = 0; bit < 16; bit++)
                if ((bits & (1 << bit)) != 0) ids.Add(AlarmBits[w][bit].Id);
        }
        return ids;
    }

    private static readonly Dictionary<int, string> DeviceStatus = new()
    {
        [0x0000] = "Standby: initializing",
        [0x0001] = "Standby: checking insulation",
        [0x0002] = "Standby: checking sunlight",
        [0x0003] = "Standby: checking the grid",
        [0x0100] = "Starting",
        [0x0200] = "On grid",
        [0x0201] = "On grid: power limited",
        [0x0202] = "On grid: derated",
        [0x0203] = "Off grid",
        [0x0300] = "Shut down: fault",
        [0x0301] = "Shut down: command",
        [0x0302] = "Shut down: OVGR",
        [0x0303] = "Shut down: communication lost",
        [0x0304] = "Shut down: power limited",
        [0x0305] = "Shut down: manual start needed",
        [0x0306] = "Shut down: DC switches off",
        [0x0307] = "Shut down: rapid shutdown",
        [0x0308] = "Shut down: not enough input power",
        [0x030C] = "Shut down: battery empty",
        [0x0401] = "Grid scheduling: cosφ-P curve",
        [0x0402] = "Grid scheduling: Q-U curve",
        [0x0403] = "Grid scheduling: PF-U curve",
        [0x0404] = "Grid scheduling: dry contact",
        [0x0405] = "Grid scheduling: Q-P curve",
        [0x0500] = "Spot check ready",
        [0x0501] = "Spot checking",
        [0x0600] = "Inspecting",
        [0x0700] = "Arc fault (AFCI) self-check",
        [0x0800] = "I-V scanning",
        [0x0900] = "Checking DC input",
        [0x0A00] = "Off grid: charging",
        [0xA000] = "Standby: no sunlight",
    };

    public static string? DescribeStatus(int value) => DeviceStatus.GetValueOrDefault(value);
}
