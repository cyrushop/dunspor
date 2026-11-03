using System;

namespace Fx9600Gpio
{
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static void Info(string message)
        {
            Write(message, null);
        }

        public static void Warn(string message)
        {
            Write(message, ConsoleColor.Yellow);
        }

        public static void Error(string message)
        {
            Write(message, ConsoleColor.Red);
        }

        private static void Write(string message, ConsoleColor? color)
        {
            lock (Gate)
            {
                var old = Console.ForegroundColor;
                if (color.HasValue) Console.ForegroundColor = color.Value;
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message);
                if (color.HasValue) Console.ForegroundColor = old;
            }
        }
    }
}
