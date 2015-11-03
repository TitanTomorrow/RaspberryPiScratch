/*
Copyright(c) 2015 Graham Chow

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.IO.Ports;

namespace PortForwarder
{
    //
    // This class simply forwards tcp traffic from localhost to another machine.
    // Unfortunately, you can't use loopback on a windows store app due to security restrictions:
    // from: https://msdn.microsoft.com/en-us/library/windows/apps/hh780593.aspx
    // "Note  Loopback is permitted only for development purposes. Usage by a Windows Runtime app installed outside of Visual Studio is not permitted. Further, a Windows Runtime app can use an IP loopback only as the target address for a client network request. So a Windows Runtime app that uses a DatagramSocket or StreamSocketListener to listen on an IP loopback address is prevented from receiving any incoming packets."
    //
    public class PortForwarder
    {
        const int BufferSize = 8092; // should be heaps as scratch uses a very short protocol
        readonly int _listeningPort; // you PC's listening port that Scratch Communicates to
        readonly string _remoteMachine; // the raspberry pi's machine address
        readonly int _remotePort; // the raspberry pi's port number
        readonly string _comport;

        // this event gets set when Sratch has sent a request to Raspberry PI
        readonly ManualResetEvent _allDone = new ManualResetEvent(true);

        // socket information...
        IPHostEntry _ipHostInfo = null;
        IPAddress _ipAddress = null;
        IPEndPoint _localEndPoint = null;
        Socket _listener = null;
        IPEndPoint _remoteEP = null;
        IPHostEntry _remoteIpHostInfo = null;
        IPAddress _remoteIpAddress = null;
        SerialPort _serialPort = null;


        // constructor should not fail, just set the parameters
        public PortForwarder(int listeningPort, string remoteMachine, int remotePort, string comport)
        {
            _listeningPort = listeningPort;
            _remoteMachine = remoteMachine;
            _remotePort = remotePort;
            _comport = comport;
        }

        // helper function to get the IP version 4 address
        private IPAddress GetIpV4Address(IPAddress [] addresses)
        {
            foreach(IPAddress addr in addresses)
            {
                if(addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    return addr;
                }
            }
            return addresses[0];
        }

        public bool SetupListenerAndRemote()
        {
            try
            {
                // get this PC's address
                _ipHostInfo = Dns.GetHostEntry(Dns.GetHostName());
                _ipAddress = GetIpV4Address(_ipHostInfo.AddressList);
                _ipAddress = IPAddress.Loopback;
                _localEndPoint = new IPEndPoint(_ipAddress, _listeningPort);
                
                // setup the listening port that listens for requests from Scratch
                _listener = new Socket(_ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                _listener.Bind(_localEndPoint);
                _listener.Listen(10);

                // get Raspberry's PI address
                if (_remoteMachine != null)
                {
                    _remoteIpHostInfo = Dns.GetHostEntry(_remoteMachine);
                    _remoteIpAddress = GetIpV4Address(_remoteIpHostInfo.AddressList);
                    _remoteEP = new IPEndPoint(_remoteIpAddress, _remotePort);
                }
                else
                {
                    _serialPort = new SerialPort();

                    // Allow the user to set the appropriate properties.
                    _serialPort.PortName = _comport;
                    _serialPort.BaudRate = 115200;
                    _serialPort.Parity = Parity.None;
                    _serialPort.DataBits = 8;
                    _serialPort.StopBits = StopBits.One;
                    _serialPort.Handshake = Handshake.None;
                    _serialPort.ReadTimeout = 500;
                    _serialPort.WriteTimeout = 500;
                    _serialPort.Open();
                }

                return true;
            }
            catch(Exception ex)
            {
                return false;
            }
        }

        //
        // Should be called when ShouldRestartListener return true
        //
        // Sets up the listener to listen to Sratch
        //
        public void StartListener()
        {
            try
            {
                // Set the event to nonsignaled state.
                _allDone.Reset();
                _listener.BeginAccept(new AsyncCallback(AcceptCallback), _listener);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }

        //
        // Poll this reguarly, Done this way so the user of this class can listen to keystrokes at the same time
        //
        public bool ShouldRestartListener(int waitTime)
        {
            return _allDone.WaitOne(waitTime);
        }

        //
        // Sratch has made a connection and wants to send the Raspberry PI a request
        //
        private void AcceptCallback(IAsyncResult ar)
        {
            // Signal the main thread to continue.
            _allDone.Set();

            try
            {
                // Get the socket that handles the client request.
                using (Socket handler = _listener.EndAccept(ar))
                {
                    // get the request data from Scratch
                    byte[] data = new byte[BufferSize];
                    int bytesRec = handler.Receive(data);
                    string request = Encoding.UTF8.GetString(data, 0, bytesRec);

                    // forward it on to the Raspberry PI and get the response
                    string response = Forward(request);

                    // send the response back to Scratch
                    byte[] msg = Encoding.ASCII.GetBytes(response.ToString());
                    handler.Send(msg);
                    handler.Shutdown(SocketShutdown.Both);
                    handler.Close();
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine(ex.Message);
            }
        }

        public string Forward(string request)
        {
            try
            {
                if (_remoteIpHostInfo != null)
                {
                    using (Socket sender = new Socket(_remoteIpAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
                    {
                        // connect to the Raspberry PI
                        sender.Connect(_remoteEP);

                        // Encode the data string into a byte array.
                        byte[] msg = Encoding.UTF8.GetBytes(request);

                        // Send the data through the socket.
                        int bytesSent = sender.Send(msg);

                        // Receive the response from the Raspberry PI.
                        byte[] bytes = new byte[BufferSize];
                        int bytesRec = sender.Receive(bytes);
                        string response = Encoding.UTF8.GetString(bytes, 0, bytesRec);

                        // Release the socket.
                        sender.Shutdown(SocketShutdown.Both);
                        sender.Close();
                        return response;
                    }
                }
                else
                {
                    string [] req = request.Split(' ');
                    if ((req.Length >= 2) && (req[0] == "GET"))
                    {
                        byte[] msg = Encoding.UTF8.GetBytes(req[1]);
                        byte[] buffer = new byte[msg.Length + 2];
                        msg.CopyTo(buffer, 2);
                        buffer[0] = 0xAA;
                        buffer[1] = (byte)(msg.Length);
                        //_serialPort.DiscardInBuffer();
                        _serialPort.Write(buffer, 0, buffer.Length);

                        if (req[1].Contains("poll"))
                        {
                            int b = _serialPort.ReadByte();
                            if (b == 0xAA)
                            {
                                int h = _serialPort.ReadByte();
                                int l = _serialPort.ReadByte();
                                int length = (h << 8) + l;
                                byte[] resp = new byte[length];
                                _serialPort.Read(resp, 0, length);
                                string response = Encoding.UTF8.GetString(resp, 0, resp.Length);
                                System.Console.WriteLine(DateTime.Now.ToLongTimeString() + " " + response);
                                return response;
                            }
                        }
                        else
                        {
                            return "okay\n";
                        }
                    }
                    return "_problem serial malformed\n";
                }
            }
            catch (Exception e)
            {
                // there was an issue, just forward it onto Scratch, it probably will be ignored
                System.Console.WriteLine(DateTime.Now.ToLongTimeString() + " " + e.Message);
                return "_problem " + e.Message + "\n";
            }
        }
    }
}
