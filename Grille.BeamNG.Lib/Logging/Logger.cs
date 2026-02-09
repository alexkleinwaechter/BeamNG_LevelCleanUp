namespace Grille.BeamNG.Logging;

public enum LoggerColor
{
    Default = ConsoleColor.Gray,
    Red = ConsoleColor.Red,
}

public static class Logger
{
    public static bool ConsoleOutputEnabled { get; set; } = false;

    static Stream? _stream;
    static StreamWriter? _writer;

    public static Stream? OutputStream { get => _stream; set
        {
            if (_stream == value)
                return;

            _stream = value;

            if (_stream == null)
            {
                _writer = null;
                return;
            }

            _writer = new StreamWriter(_stream);
        }
    }

    public static void WriteLine()
    {
        WriteToStream();
        WriteToConsole();
    }

    public static void WriteLine(string text)
    {
        WriteToStream(text);
        WriteToConsole(text);
    }

    public static void WriteLine(string text, LoggerColor color)
    {
        WriteToStream(text);
        WriteToConsole(text, color);
    }

    static void WriteToStream()
    {
        if (_writer == null)
            return;

        _writer.WriteLine();
    }

    static void WriteToStream(string text)
    {
        if (_writer == null)
            return;

        _writer.WriteLine(text);
        _writer.Flush();
    }

    static void WriteToConsole()
    {
        if (!ConsoleOutputEnabled)
            return;

        Console.WriteLine();
    }

    static void WriteToConsole(string text, LoggerColor color = LoggerColor.Default)
    {
        if (!ConsoleOutputEnabled)
            return;

        Console.ForegroundColor = (ConsoleColor)color;
        Console.WriteLine(text);
        Console.ForegroundColor = (ConsoleColor)LoggerColor.Default;
    }
}
