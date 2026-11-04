/* RIGHTS-FINAL: run the SEIT's six asset-receipt commands from their single
 * definition. Fixture negatives reject a mutation of otherwise valid data.
 * Resource group isolated-csharp; no Unity editor, display or network.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

public static class RightsFinalChecks
{
    public static int Main()
    {
        try
        {
            var expected = new HashSet<string>();
            using (var seit = JsonDocument.Parse(File.ReadAllText("docs/plans/full-game-rev2/m4/test-engineer.json")))
                foreach (var task in seit.RootElement.GetProperty("tasks").EnumerateArray())
                    if (task.GetProperty("id").GetString() == "RIGHTS-FINAL")
                        foreach (var id in task.GetProperty("spec_commands").EnumerateArray())
                            expected.Add(id.GetString());
            using (var doc = JsonDocument.Parse(File.ReadAllText("production/preflight/RIGHTS-FINAL.json")))
            {
                var commands = doc.RootElement.GetProperty("commands");
                var actual = new HashSet<string>();
                foreach (var command in commands.EnumerateArray())
                    actual.Add(command.GetProperty("id").GetString());
                if (expected.Count != 6 || commands.GetArrayLength() != expected.Count || !actual.SetEquals(expected))
                    throw new Exception("expected exactly the six SEIT command ids");
                bool failed = false;
                foreach (var command in commands.EnumerateArray())
                {
                    var argv = command.GetProperty("argv").EnumerateArray(); argv.MoveNext();
                    var start = new ProcessStartInfo(argv.Current.GetString()) { UseShellExecute = false,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    while (argv.MoveNext()) start.ArgumentList.Add(argv.Current.GetString());
                    using (var child = Process.Start(start))
                    {
                        child.StandardInput.Close();
                        Console.Write(child.StandardOutput.ReadToEnd());
                        Console.Write(child.StandardError.ReadToEnd());
                        child.WaitForExit(); failed |= child.ExitCode != 0;
                    }
                }
                if (failed) return 1;
            }
            Console.WriteLine("RIGHTS-FINAL ALL PASS"); return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL RIGHTS-FINAL " + e.Message); return 1; }
    }
}
