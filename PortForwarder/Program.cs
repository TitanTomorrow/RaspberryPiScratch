/*
Copyright(c) 2015 Graham Chow

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace PortForwarder
{
    class Program
    {
        static PortForwarder pf;

        static void Main(string[] args)
        {
            int port = 50210, remotePort = 80;
            string remoteMachine = null;
            string comport = null;

            try
            {
                if (args[0].Contains("COM"))
                {
                    comport = args[0];
                }
                else
                {
                    remoteMachine = args[0];
                }
                try
                {
                    if (remoteMachine != null)
                    {
                        port = Convert.ToInt32(args[2]);
                        remotePort = Convert.ToInt32(args[1]);
                    }
                    else
                    {
                        port = Convert.ToInt32(args[1]);
                    }
                }
                catch
                {
                    port = 50210;
                    remotePort = 80;
                }
            }
            catch
            {
                System.Console.WriteLine("PortForwarder <remote machine ip address> or <COMX> [remote machine port] [local machine's port number]");
                System.Console.WriteLine("eg PortForwarder 192.168.1.17 80 50210");
                System.Console.WriteLine("eg PortForwarder 192.168.1.17");
                System.Console.WriteLine("eg PortForwarder COM12");
                return;
            }

            if (remoteMachine != null)
            {
                System.Console.WriteLine("remote machine:" + remoteMachine);
                System.Console.WriteLine("remote port:" + remotePort);
            }
            else
            {
                System.Console.WriteLine("comm port:" + comport);
            }
            System.Console.WriteLine("port:" + port);
            System.Console.WriteLine("press 'q' to quit");

            pf = new PortForwarder(port, remoteMachine, remotePort, comport);
            if (pf.SetupListenerAndRemote())
            {
                for (;;)
                {
                    if (pf.ShouldRestartListener(10))
                    {
                        pf.StartListener();
                    }
                    if (Console.KeyAvailable)
                    {
                        ConsoleKeyInfo key = Console.ReadKey();
                        if (key.KeyChar == 'q')
                            return;
                    }
                }
            }
        }
    }
}

