﻿using gsudo.Helpers;
using gsudo.Rpc;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading.Tasks;

namespace gsudo.ProcessRenderers
{
    // Regular Console app (WindowsPTY via .net) Client. (not ConPTY)
    class VTClientRenderer : IProcessRenderer
    {
        static readonly string[] TOKENS = new string[] { "\x001B[6n", Constants.TOKEN_EXITCODE }; //"\0", "\f", Globals.TOKEN_ERROR, , Globals.TOKEN_FOCUS, Globals.TOKEN_KEY_CTRLBREAK, Globals.TOKEN_KEY_CTRLC };
        private readonly gsudo.Rpc.Connection _connection;
        private readonly ElevationRequest _elevationRequest;

        public static int? ExitCode { get; private set; }
        int consecutiveCancelKeys = 0;
        private bool expectedClose; 

        public VTClientRenderer(Connection connection, ElevationRequest elevationRequest)
        {
            _connection = connection;
            _elevationRequest = elevationRequest;
        }

        public async Task<int> Start()
        {
            ConsoleHelper.EnableVT();


            try
            {
                Console.CancelKeyPress += CancelKeyPressHandler;

                var t1 = new StreamReader(_connection.DataStream, GlobalSettings.Encoding)
                    .ConsumeOutput((s) => WriteToConsole(s));
                var t2 = new StreamReader(_connection.ControlStream, GlobalSettings.Encoding)
                    .ConsumeOutput((s) => HandleControlData(s));

                int i = 0;
                while (_connection.IsAlive)
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    try
                    {
                        if (Console.KeyAvailable)
                        {
                            // send input character-by-character to the pipe
                            var key = Console.ReadKey(intercept: true);
                            var keychar = key.KeyChar;
                            if (key.Key == ConsoleKey.LeftArrow)
                                await _connection.DataStream.WriteAsync("\x001B[1D").ConfigureAwait(false);
                            else if (key.Key == ConsoleKey.RightArrow)
                                await _connection.DataStream.WriteAsync("\x001B[1C").ConfigureAwait(false);
                            else if (key.Key == ConsoleKey.UpArrow)
                                await _connection.DataStream.WriteAsync("\x001B[1A").ConfigureAwait(false);
                            else if (key.Key == ConsoleKey.DownArrow)
                                await _connection.DataStream.WriteAsync("\x001B[1B").ConfigureAwait(false);
                            else
                                await _connection.DataStream.WriteAsync(keychar.ToString()).ConfigureAwait(false);
                        }

                        i = (i + 1) % 50;
                        if (i == 0) await _connection.ControlStream.WriteAsync("\0").ConfigureAwait(false); // Sending a KeepAlive is mandatory to detect if the pipe has disconnected.
                    }
                    catch (ObjectDisposedException)
                    {
                        break; 
                    }
                    catch (IOException)
                    {
                        break;
                    }
                }

                await _connection.FlushAndCloseAll().ConfigureAwait(false);

                if (ExitCode.HasValue && ExitCode.Value == 0 && GlobalSettings.NewWindow)
                {
                    Logger.Instance.Log($"Elevated process started successfully", LogLevel.Debug);
                    return 0;
                }
                else if (ExitCode.HasValue)
                {
                    Logger.Instance.Log($"Elevated process exited with code {ExitCode}", ExitCode.Value == 0 ? LogLevel.Debug : LogLevel.Info);
                    return ExitCode.Value;
                }
                else if (expectedClose)
                {
                    Logger.Instance.Log($"Connection closed by the client.", LogLevel.Debug);
                    return 0;
                }
                else
                {
                    Logger.Instance.Log($"Connection from server lost.", LogLevel.Warning);
                    return Constants.GSUDO_ERROR_EXITCODE;
                }
            }
            finally
            {
                Console.CancelKeyPress -= CancelKeyPressHandler;
            }

        }

        private void CancelKeyPressHandler(object sender, ConsoleCancelEventArgs e)
        {
            string CtrlC_Command = "\x3";
            e.Cancel = true;
            if (!_connection.IsAlive) return;

            if (++consecutiveCancelKeys > 3 || e.SpecialKey == ConsoleSpecialKey.ControlBreak)
            {
                _connection.FlushAndCloseAll();
                expectedClose = true;
                return;
            }

            // restart console input.
            //var t1 = new StreamReader(Console.OpenStandardInput()).ConsumeOutput((s) => IncomingKey(s, pipe));

            if (++consecutiveCancelKeys > 2)
            {
                Logger.Instance.Log("Press CTRL-C again to stop gsudo\r\n", LogLevel.Warning);
                var b = GlobalSettings.Encoding.GetBytes(CtrlC_Command);
                _connection.DataStream.Write(b, 0, b.Length);
            }
            else
            {
                var b = GlobalSettings.Encoding.GetBytes(CtrlC_Command);
                _connection.DataStream.Write(b, 0, b.Length);
            }
        }


        private async Task WriteToConsole(string s)
        {
            try
            {
                if (s == "\x001B[6n")
                {
                    await _connection.DataStream.WriteAsync($"\x001B[{Console.CursorTop};{Console.CursorLeft}R");
                    return;
                }

                Console.Write(s);
            }
            catch (Exception ex)
            {
                Logger.Instance.Log(ex.ToString(), LogLevel.Error);
            }
        }


        enum Mode { Normal, Focus, Error, ExitCode };
        Mode CurrentMode = Mode.Normal;
        
        private async Task HandleControlData(string s)
        {
            Action<Mode> Toggle = (m) => CurrentMode = CurrentMode == Mode.Normal ? m : Mode.Normal;
            
            var tokens = new Stack<string>(StringTokenizer.Split(s, TOKENS).Reverse());

            while (tokens.Count > 0)
            {
                var token = tokens.Pop();

                if (token == "\0") continue; // session keep alive
                if (token == Constants.TOKEN_EXITCODE)
                {
                    Toggle(Mode.ExitCode);
                    continue;
                }
                if (CurrentMode == Mode.ExitCode)
                {
                    ExitCode = int.Parse(token, System.Globalization.CultureInfo.InvariantCulture);
                    continue;
                }
                /*
                if (token == "\f")
                {
                    Console.Clear();
                    continue;
                }
                if (token == Globals.TOKEN_FOCUS)
                {
                    Toggle(Mode.Focus);
                    continue;
                }

                if (token == Globals.TOKEN_ERROR)
                {
                    //fix intercalation of messages;
                    await Console.Error.FlushAsync();
                    await Console.Out.FlushAsync();

                    Toggle(Mode.Error);
                    if (CurrentMode == Mode.Error)
                        Console.ForegroundColor = ConsoleColor.Red;
                    else
                        Console.ResetColor();
                    continue;
                }

                if (CurrentMode == Mode.Focus)
                {
                    var hwnd = (IntPtr)int.Parse(token, CultureInfo.InvariantCulture);
                    Globals.Logger.Log($"SetForegroundWindow({hwnd}) returned {ProcessStarter.SetForegroundWindow(hwnd)}", LogLevel.Debug);
                    continue;
                }
                if (CurrentMode == Mode.Error)
                {
//                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.Write(token);
//                    Console.ResetColor();
                    continue;
                }

                */
            }
                
            return;
        }

        private async Task IncomingKey(string s, NamedPipeClientStream pipe )
        {
            consecutiveCancelKeys = 0;
            await pipe.WriteAsync(s).ConfigureAwait(false);
        }
    }
}