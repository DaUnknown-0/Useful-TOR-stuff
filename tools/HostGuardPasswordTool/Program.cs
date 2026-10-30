using HostGuardSecurity;
using System.Text;

try
{
    if (args.Length != 1 || Console.IsInputRedirected)
        throw new InvalidOperationException("Run interactively with exactly one output-file argument.");
    Console.WriteLine("Choose a long, unique lobby password. Input is hidden.");
    string ReadPassword(string label)
    {
        Console.Write(label);
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException();
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
        }
        Console.WriteLine();
        return text.ToString();
    }
    string password = ReadPassword("Lobby password: ");
    string confirmation = ReadPassword("Repeat password: ");
    if (!string.Equals(password, confirmation, StringComparison.Ordinal)) throw new InvalidOperationException("Passwords do not match; no file changed.");
    string encoded = HostPasswordHash.Create(password);
    string output = Path.GetFullPath(args[0]);
    string temporary = output + ".tmp";
    File.WriteAllText(temporary, encoded, new UTF8Encoding(false));
    File.Move(temporary, output, true);
    Console.WriteLine("New verifier written. No password or verifier was printed. Nothing was uploaded.");
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled; no verifier written."); Environment.ExitCode = 1; }
catch (Exception error) { Console.Error.WriteLine(error is ArgumentException or InvalidOperationException ? error.Message : "Verifier generation failed; check the output location and dependencies."); Environment.ExitCode = 1; }
