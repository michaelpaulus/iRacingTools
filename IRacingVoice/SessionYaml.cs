using System.Text.RegularExpressions;

/// <summary>
/// The bits of iRacing's session info (YAML) the voice agent needs: drivers and car numbers, camera groups and the
/// car's tyre compounds. Plain regexes, like <see cref="SessionFacts"/>: the layout is fixed and only a few fields matter.
/// </summary>
public static class SessionYaml
{
    public sealed record Car(int Idx, string Name, string Number, string Class, bool IsPaceCar, bool IsSpectator);

    public static int PlayerIdx(string yaml) =>
        Regex.Match(yaml, @"\n[ \t]*DriverCarIdx:[ \t]*(\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value) : -1;

    public static List<Car> Cars(string yaml)
    {
        var cars = new List<Car>();
        foreach (var block in Items(Section(yaml, "Drivers"), "CarIdx"))
        {
            int idx = int.Parse(Field(block, "CarIdx"));
            cars.Add(new Car(idx, Field(block, "UserName"), Field(block, "CarNumber").Trim('"'), Field(block, "CarClassShortName"),
                Field(block, "CarIsPaceCar") == "1", Field(block, "IsSpectator") == "1"));
        }
        return cars;
    }

    /// <summary>Camera groups ("Nose", "Cockpit", "TV1", ...) by number.</summary>
    public static List<(int Num, string Name)> CameraGroups(string yaml) =>
        Items(Section(yaml, "Groups"), "GroupNum").Select(b => (int.Parse(Field(b, "GroupNum")), Field(b, "GroupName"))).ToList();

    /// <summary>The tyre compounds this car can use, by the index iRacing's pit command takes.</summary>
    public static List<(int Index, string Type)> TireCompounds(string yaml) =>
        Items(Section(yaml, "DriverTires"), "TireIndex").Select(b => (int.Parse(Field(b, "TireIndex")), Field(b, "TireCompoundType").Trim('"'))).ToList();

    /// <summary>"Race", "Practice", "Lone Qualify", ... for the session with this SessionNum.</summary>
    public static string SessionType(string yaml, int sessionNum) =>
        Items(Section(yaml, "Sessions"), "SessionNum").Where(b => Field(b, "SessionNum") == sessionNum.ToString())
            .Select(b => Field(b, "SessionType")).FirstOrDefault() ?? "";

    // A list under "Name:", up to the next line that's indented no deeper than the key.
    static string Section(string yaml, string key)
    {
        var m = Regex.Match(yaml, $@"\n([ \t]*){key}:[ \t]*\r?\n");
        if (!m.Success) return "";
        int indent = m.Groups[1].Value.Length, start = m.Index + m.Length;
        var end = new Regex($@"\n[ \t]{{0,{indent}}}[A-Za-z]").Match(yaml, start);
        return yaml[start..(end.Success ? end.Index : yaml.Length)];
    }

    static IEnumerable<string> Items(string list, string firstKey) =>
        Regex.Matches(list, $@"-[ \t]*{firstKey}:.*?(?=\n[ \t]*-[ \t]*{firstKey}:|\z)", RegexOptions.Singleline).Select(m => m.Value);

    static string Field(string block, string key) =>
        Regex.Match(block, $@"(?:^|\n)[ \t-]*{key}:[ \t]*([^\r\n]*)") is { Success: true } m ? m.Groups[1].Value.Trim() : "";
}
