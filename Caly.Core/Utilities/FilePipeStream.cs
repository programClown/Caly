﻿// Copyright (c) 2025 BobLd
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Caly.Core.Utilities
{
    /// <summary>
    /// Pipe stream to communicate between application instances on the machine.
    /// </summary>
    public sealed class FilePipeStream : IDisposable, IAsyncDisposable
    {
        // https://googleprojectzero.blogspot.com/2019/09/windows-exploitation-tricks-spoofing.html

        private static readonly string _pipeName = "caly_pdf_files.pipe";

        private static ReadOnlySpan<byte> _keyPhrase => "ca1y k3y pa$$"u8;

        private static readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(2);

        private readonly NamedPipeServerStream _pipeServer;

        public FilePipeStream()
        {
#if DEBUG
            if (Avalonia.Controls.Design.IsDesignMode)
            {
                _pipeServer = new(Guid.NewGuid().ToString(), PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                return;
            }
#endif
            _pipeServer = new(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
        }

        public async IAsyncEnumerable<string?> ReceivePathAsync([EnumeratorCancellation] CancellationToken token)
        {
            while (true)
            {
                Memory<byte> pathBuffer = Memory<byte>.Empty;
                try
                {
                    token.ThrowIfCancellationRequested();
                    
                    // https://learn.microsoft.com/en-us/dotnet/standard/io/how-to-use-named-pipes-for-network-interprocess-communication
                    await _pipeServer.WaitForConnectionAsync(token);

                    Memory<byte> lengthBuffer = new byte[2];
                    if (await _pipeServer.ReadAsync(lengthBuffer, token) != 2)
                    {
                        // TODO - Log
                        continue;
                    }

                    var len = BitConverter.ToUInt16(lengthBuffer.Span);

                    // Read key phrase
                    Memory<byte> keyBuffer = new byte[_keyPhrase.Length];
                    if (await _pipeServer.ReadAsync(keyBuffer, token) != _keyPhrase.Length)
                    {
                        // TODO - Log
                        continue;
                    }

                    // Check key phrase
                    if (!keyBuffer.Span.SequenceEqual(_keyPhrase))
                    {
                        // TODO - Log
                        continue;
                    }

                    // Read message type
                    Memory<byte> byteBuffer = new byte[1];
                    if (await _pipeServer.ReadAsync(byteBuffer, token) != 1)
                    {
                        // TODO - Log
                        continue;
                    }

                    switch ((PipeMessageType)byteBuffer.Span[0])
                    {
                        case PipeMessageType.FilePath:
                            {
                                // Read file path
                                pathBuffer = new byte[len];

                                if (await _pipeServer.ReadAsync(pathBuffer, token) != len)
                                {
                                    // TODO - Log
                                    continue;
                                }
                            }
                            break;

                        case PipeMessageType.Command:
                            {
                                byteBuffer.Span.Clear();
                                if (await _pipeServer.ReadAsync(byteBuffer, token) != 1)
                                {
                                    // TODO - Log
                                    continue;
                                }

                                ProcessMessageCommand((PipeCommandMessageType)byteBuffer.Span[0]);
                            }
                            break;

                        default:
                            // TODO - Log
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    // No op
                }
                catch (Exception e)
                {
                    Debug.WriteExceptionToFile(e);
                    throw;
                }
                finally
                {
                    // We are not connected if operation was canceled
                    if (_pipeServer.IsConnected)
                    {
                        _pipeServer.Disconnect();
                    }
                }

                if (pathBuffer.Length > 0)
                {
                    yield return Encoding.UTF8.GetString(pathBuffer.Span);
                }
            }
        }

        private static void ProcessMessageCommand(PipeCommandMessageType commandType)
        {
            switch (commandType)
            {
                case PipeCommandMessageType.BringToFront:
                    App.Current?.TryBringToFront();
                    break;

                default:
                    // TODO - Log
                    break;
            }
        }

        public void Dispose()
        {
            _pipeServer.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await _pipeServer.DisposeAsync();
        }

        public static bool SendBringToFront()
        {
            try
            {
                using (var pipeClient = new NamedPipeClientStream(".", _pipeName,
                           PipeDirection.Out, PipeOptions.CurrentUserOnly,
                           TokenImpersonationLevel.Identification))
                {
                    pipeClient.Connect(_connectTimeout); // If you are getting a timeout in debug mode, just re-run Caly

                    Memory<byte> lengthBytes = BitConverter.GetBytes((ushort)1);
                    pipeClient.Write(lengthBytes.Span);
                    pipeClient.Write(_keyPhrase);
                    pipeClient.WriteByte((byte)PipeMessageType.Command);
                    pipeClient.WriteByte((byte)PipeCommandMessageType.BringToFront);

                    pipeClient.Flush();
                }

                return true;
            }
            catch (UnauthorizedAccessException uae)
            {
                // Server must be running in admin, but not the client
                // Handle the case and display error message
                Debug.WriteExceptionToFile(uae);
                throw;
            }
            catch (TimeoutException toe)
            {
                // Could not connect to the running instance of Caly
                // probably because it is actually not running, i.e. the 
                // lock file was not properly deleted after close
                Debug.WriteExceptionToFile(toe);
                CalyFileMutex.ForceReleaseMutex();
                throw ThrowOnTimeoutException(toe);
            }
            catch (Exception e)
            {
                Debug.WriteExceptionToFile(e);
                throw;
            }
        }

        public static bool SendPath(string filePath)
        {
            try
            {
                using (var pipeClient = new NamedPipeClientStream(".", _pipeName,
                           PipeDirection.Out, PipeOptions.CurrentUserOnly,
                           TokenImpersonationLevel.Identification))
                {
                    pipeClient.Connect(_connectTimeout);

                    Memory<byte> pathBytes = Encoding.UTF8.GetBytes(filePath);
                    if (pathBytes.Length > ushort.MaxValue)
                    {
                        throw new PathTooLongException($"The pdf file path passed to Caly is too long. Received {pathBytes.Length} bytes, and maximum size is {ushort.MaxValue}.");
                    }

                    Memory<byte> lengthBytes = BitConverter.GetBytes((ushort)pathBytes.Length);
                    pipeClient.Write(lengthBytes.Span);
                    pipeClient.Write(_keyPhrase);
                    pipeClient.WriteByte((byte)PipeMessageType.FilePath);
                    pipeClient.Write(pathBytes.Span);

                    pipeClient.Flush();
                }

                return true;
            }
            catch (UnauthorizedAccessException uae)
            {
                // Server must be running in admin, but not the client
                // Handle the case and display error message
                Debug.WriteExceptionToFile(uae);
                throw;
            }
            catch (TimeoutException toe)
            {
                // Could not connect to the running instance of Caly
                // probably because it is actually not running, i.e. the 
                // lock file was not properly deleted after close
                Debug.WriteExceptionToFile(toe);
                CalyFileMutex.ForceReleaseMutex();
                throw ThrowOnTimeoutException(toe);
            }
            catch (Exception e)
            {
                Debug.WriteExceptionToFile(e);
                throw;
            }
        }

        private static CalyCriticalException ThrowOnTimeoutException(TimeoutException toe)
        {
            return new CalyCriticalException("Could not connect to the running instance of Caly," +
                                            " probably because it is actually not running, i.e. the" +
                                            " Caly lock was not properly removed after close.", toe)
            {
                TryRestartApp = true
            };
        }

        private enum PipeMessageType : byte
        {
            None = 0,
            FilePath = 1,
            Command = 2
        }

        private enum PipeCommandMessageType : byte
        {
            None = 0,
            BringToFront = 1
        }
    }
}
