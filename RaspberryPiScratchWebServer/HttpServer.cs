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
using System.Net.Http;
using Windows.Foundation.Collections;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.AppService;
using Windows.System.Threading;
using Windows.Networking.Sockets;
using System.IO;
using Windows.Storage.Streams;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.UI.Xaml;
using System.Diagnostics;
using System.Threading;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.SerialCommunication;

namespace RaspberryPiScratchWebServer
{
    //
    // This is the event that gets raised to notify the UI of a state or distance change
    //
    internal class HttpServerStatusArgs : EventArgs
    {
        public HttpServerStatusArgs(int peripheralId, string status, int distance)
        {
            Status = status;
            Distance = distance;
            PeripheralId = peripheralId;
        }

        public int PeripheralId { get; set; }
        public int Distance { get; set; }
        public string Status { get; set; }
    }

    internal delegate void HttpServerStatusUpdate(object sender, HttpServerStatusArgs e);

    //
    // The "web server" that Scratch ultimately talks to
    //
    public sealed class HttpServer : IDisposable
    {
        enum MOTOR_STATE : int
        {
            STOPPED = 0,
            FORWARD = 1,
            REVERSE = 2
        };

        // the bit mask of the pins that the motor is assigned to on the PiFace board
        readonly private byte[] MOTOR_BIT_FORWARD = new byte[2] { 0x08, 0x10 };
        readonly private byte[] MOTOR_BIT_BACK = new byte[2] { 0x20, 0x40 };
        // operates the relay that supplies power to the L298N motor driver board
        // this is needed because we only want to apply power to the motors once everything is initialised 
        const byte MOTOR_POWER = 0x1;

        // the spi interface to the pi face board
        readonly PiFaceSpiDriver _spiDriver = new PiFaceSpiDriver();

        // the serial interface to the external bluetooth device
        private SerialDevice _serialDevice = null;
        private DataWriter _dw = null;
        private DataReader _dr = null;
        // I blew up the gertbot board, so can't use it anymore
        //readonly GertbotUartDriver _gertbot = new GertbotUartDriver();

        // details of the TCP port
        private const uint BufferSize = 8192;
        private int port;
        private readonly StreamSocketListener _listener;

        // distance measurement
        int _ultrasonicDistance = 0;
        Stopwatch _stopWatch = new Stopwatch();
        private Timer _timer = null;

        // motor timer so we can pulse it to get different power ratings
        private Timer _motorTimer = null;

        // piface and motor state
        private byte _gpa = 0;
        private object _gpaAObject = new object();
        private MOTOR_STATE [] _motorState = new MOTOR_STATE[2] { MOTOR_STATE.STOPPED, MOTOR_STATE.STOPPED };
        private int [] _motorOnCount = new int[2] { 0, 0 }; // number of milliseconds the motor is on
        private int [] _motorOffCount = new int[2] { 10, 10 }; // number of milliseconds the motor is off, the total makes the period
        private int _motorCount = 0; // continously counts up every millisecond

        // event for the UI to subscribe to
        internal event EventHandler<HttpServerStatusArgs> StatusUpdate;

        public HttpServer(int serverPort)
        {
            // initialise everything and subscribe to events
            _listener = new StreamSocketListener();
            port = serverPort;
            _listener.ConnectionReceived += (s, e) => ProcessRequestAsync(e.Socket);
            _spiDriver.PiFaceInterrupt += Spi_driver_PiFaceInterrupt;
            //_gertbot.GertbotInterrupt += _gertbot_GertbotInterrupt;
        }

        // this is called periodically to always get the distance 
        private void _timerCallabck(object sender)
        {
            SendPulse();
        }

        // send the pulse to the ultrasound sensor to trigger it's ultrasound burst
        private void SendPulse()
        {
            // start timer and turn ultrasonic burst on
            _stopWatch.Reset();
            lock (_gpaAObject)
            {
                _gpa &= 0x7F; 
            }
            _spiDriver.WriteGPA(_gpa);
            Task.Delay(TimeSpan.FromTicks(100)).Wait();
            // turn trigger off.
            lock (_gpaAObject)
            {
                _gpa |= 0x80;
            }
            _spiDriver.WriteGPA(_gpa);
            _stopWatch.Restart();

            // wait for the response
            // this is really really inaccurate, millisecond accuracy
            long tick = DateTime.Now.Ticks;
            long delta = TimeSpan.TicksPerMillisecond * 60;
            while(DateTime.Now.Ticks - tick < delta)
            {
                byte v = _spiDriver.ReadGPB();
                if((v & 0x80) == 0)
                {
                    double milliseconds = _stopWatch.ElapsedMilliseconds;
                    if (milliseconds != 0)
                    {
                        double dist = milliseconds * 0.170;
                        // just going to ignore anything above 1m as noise
                        if (dist <= 1.0)
                        {
                            _ultrasonicDistance = (int)(Math.Round(dist * 1000));
                            if (StatusUpdate != null)
                            {
                                StatusUpdate(this, new HttpServerStatusArgs(0, null, _ultrasonicDistance));
                            }
                        }
                        return;
                    }
                }
            }
        }

        // blew up gertbot board, so commenting out
        //private void _gertbot_GertbotInterrupt(object sender, GertbotEventArgs e)
        //{
        //    if (StatusUpdate != null)
        //    {
        //        StatusUpdate(this, new HttpServerStatusArgs(1, "hardware update", 0));
        //    }
        //}

        public async void InitialiseHardware()
        {
            // PI Face intialisation
            await _spiDriver.InitHardware();
            //await _gertbot.InitHardware();

            // bluetooth serial
            string aqs = SerialDevice.GetDeviceSelector();
            DeviceInformationCollection devices_info = await DeviceInformation.FindAllAsync(aqs);

            // making an assumption here that the serial we want is #1
            // this will only work with Windows IOT version Insider Preview Build Number 10556.0
            _serialDevice = await SerialDevice.FromIdAsync(devices_info[0].Id);
            if (_serialDevice != null)
            {
                // Configure serial settings
                _serialDevice.WriteTimeout = TimeSpan.FromMilliseconds(1000);
                _serialDevice.ReadTimeout = TimeSpan.FromMilliseconds(1000);
                _serialDevice.BaudRate = 115200;
                _serialDevice.Parity = SerialParity.None;
                _serialDevice.StopBits = SerialStopBitCount.One;
                _serialDevice.DataBits = 8;
                _serialDevice.Handshake = SerialHandshake.None;
                _dw = new DataWriter(_serialDevice.OutputStream);
                _dr = new DataReader(_serialDevice.InputStream);
                _dr.InputStreamOptions = InputStreamOptions.None;

                // start the read thread
                StartReadThread();
            }
            // turn the relay on and power up the motordriver
            SetBitState(MOTOR_POWER, 0);
        }

        // thread that continously reads the serial port to get commands from Scratch
        public void StartReadThread()
        {
            Task.Run(async () =>
            {
                while (_serialDevice != null)
                {
                    try
                    {
                        bool success = await ReadNextMessage();
                    }
                    catch(Exception ex)
                    {
                        ex.ToString();
                    }
                }
            });
        }

        // Read the next message
        public async Task<bool> ReadNextMessage()
        {
            Task<UInt32> loadAsyncTask;
            loadAsyncTask = _dr.LoadAsync(1).AsTask();
            UInt32 bytes_remaining = await loadAsyncTask;
            bool header_found = false;
            while (bytes_remaining > 0)
            {
                // get header byte
                // since everything is meant to eh ascii, looking for 0xAA as a header is safe.
                byte b = _dr.ReadByte();
                bytes_remaining--;
                if (b == 0xAA)
                {
                    header_found = true;
                    break;
                }
            }
            if(header_found)
            {
                // read length of packet
                if(bytes_remaining == 0)
                {
                    loadAsyncTask = _dr.LoadAsync(1).AsTask();
                    bytes_remaining += await loadAsyncTask;
                }
                if(bytes_remaining > 0)
                {
                    int length = _dr.ReadByte();
                    bytes_remaining--;
                    // read the rest of the packet
                    if(bytes_remaining < length)
                    {
                        loadAsyncTask = _dr.LoadAsync((uint)(length- bytes_remaining)).AsTask();
                        bytes_remaining += await loadAsyncTask;
                    }
                    if (bytes_remaining >= length)
                    {
                        byte[] data = new byte[length];
                        _dr.ReadBytes(data);
                        string request = Encoding.UTF8.GetString(data, 0, data.Length);

                        // actually process the command
                        string response = ProcessCommand(request);
                        if (request.Contains("poll"))
                        {
                            // only respond if this is a poll
                            byte[] resultArray = Encoding.UTF8.GetBytes(response);
                            data = new byte[resultArray.Length + 3];
                            resultArray.CopyTo(data, 3);
                            data[0] = 0xAA;
                            data[1] = (byte)(resultArray.Length >> 8);
                            data[2] = (byte)(resultArray.Length & 0xFF);
                            _dw.WriteBytes(data);
                            Task<UInt32> storeAsyncTask = _dw.StoreAsync().AsTask();
                            UInt32 bytesWritten = await storeAsyncTask;
                        }
                        return true;
                    }
                }
            }
            return false;
        }

        // PI face change event
        private void Spi_driver_PiFaceInterrupt(object sender, PiFaceEventArgs e)
        {
            if (StatusUpdate != null)
            {
                StatusUpdate(this, new HttpServerStatusArgs(0, "hardware update", 0));
            }
            if (_spiDriver.HardwareOk() && (_timer == null))
            {
                _timer = new Timer(_timerCallabck, this, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
                _stopWatch.Start();
                _motorTimer = new Timer(_motorTimerCallabck, this, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));
            }
        }

        // used by the UI to indicate the hardware is ok
        public bool HardwareOk(int peripheralId)
        {
            if (peripheralId == 0)
                return _spiDriver.HardwareOk();
            //else if (peripheralId == 1)
            //    return _gertbot.HardwareOk();
            else
                return false;

        }

        //
        // start the actual server
        //
        public void StartServer()
        {
#pragma warning disable CS4014
            _listener.BindServiceNameAsync(port.ToString());
#pragma warning restore CS4014
        }

        //
        // Process the incomming request
        //
        private async void ProcessRequestAsync(StreamSocket socket)
        {
            // read the request from the socket
            string request = "";
            using (IInputStream input = socket.InputStream)
            {
                byte[] data = new byte[BufferSize];
                IBuffer buffer = data.AsBuffer();
                await input.ReadAsync(buffer, BufferSize, InputStreamOptions.Partial);
                request = Encoding.UTF8.GetString(data, 0, data.Length);
            }

            // parse the request into the GET and payload
            string requestMethod = request.ToString().Split('\n')[0];
            string[] requestParts = requestMethod.Split(' ');

            // only process if this is a GET
            string response = null;
            if ((requestParts.Length > 0) && (requestParts[0] == "GET"))
            {
                if (requestParts.Length > 1)
                {
                    // display the request on the screen for debugging
                    if(StatusUpdate != null)
                    {
                        StatusUpdate(this, new HttpServerStatusArgs(0, requestParts[1], 0));
                    }
                    // process the request
                    response = ProcessCommand(requestParts[1]);
                }
            }
            if(response == null)
            {
                response = "_problem unknown error\n";
            }

            // send back the response
            using (IOutputStream output = socket.OutputStream)
            {
                using (Stream resp = output.AsStreamForWrite())
                {
                    if (response == null)
                    {
                        string result = String.Format("HTTP/1.1 500 Internal Server Error\r\n" +
                            "Content-Type: text/html; charset=ISO-8859-1\r\n" +
                            "Content-Length: 0\r\n" +
                            "Connection: close\r\n\r\n");
                        byte[] resultArray = Encoding.UTF8.GetBytes(result);
                        await resp.WriteAsync(resultArray, 0, resultArray.Length);
                    }
                    else
                    {
                        string result = String.Format("HTTP/1.1 200 OK\r\n" +
                            "Content-Type: text/html; charset=ISO-8859-1\r\n" +
                            "Content-Length: {0}\r\n" +
                            "Access-Control-Allow-Origin: *\r\n" +
                            "Connection: close\r\n\r\n" +
                            response,
                            response.Length);
                        byte[] resultArray = Encoding.UTF8.GetBytes(result);
                        await resp.WriteAsync(resultArray, 0, resultArray.Length);
                    }
                }
            }
        }

        //
        // process the actual command
        // these are defined in the RaspberryPi.s2e file
        //
        private string ProcessCommand(string request)
        {
            try
            {
                // break down the request into command and parameters
                string[] command_breakdown = request.Split('/');
                string command = "";
                string[] parameters = null;
                if (command_breakdown.Length >= 2)
                    command = command_breakdown[1];
                if (command_breakdown.Length > 2)
                {
                    parameters = command_breakdown.Skip(2).ToArray();
                }

                switch (command)
                {
                    // called by Scratch around 30 times per second
                    case "poll":
                        if (_spiDriver.HardwareOk() == false)
                            return "_problem The Digital 2 Piface is not connected.\n";
                        else
                        {
                            StringBuilder sb = new StringBuilder();
                            int gpb = _spiDriver.ReadGPB();
                            int gpa = _spiDriver.ReadGPA();

                            // poll the input and output pins
                            int mask = 1;
                            for (int i = 0; i < 7; i++)
                            {
                                sb.Append(String.Format("i/{0} {1}\n", i, ((gpb & mask) != 0) ? "true" : "false"));
                                if ((i == 1) || (i == 2))
                                {
                                    sb.Append(String.Format("o/{0} {1}\n", i-1, ((gpa & mask) != 0) ? "true" : "false"));
                                }
                                mask <<= 1;
                            }
                            sb.Append(String.Format("d {0}\n", _ultrasonicDistance));
                            
                            return sb.ToString();
                        }
                    // set the output for a pin
                    case "o":
                        if (parameters.Length == 2)
                        {
                            int bit = Convert.ToInt32(parameters[0]);
                            bit++;
                            if ((bit >= 1) && (bit <= 2))
                            { 
                                bool state = Convert.ToBoolean(parameters[1]);

                                lock(_gpaAObject)
                                {
                                    if (state)
                                        _gpa = (byte)(_gpa | (1 << bit));
                                    else
                                        _gpa = (byte)(_gpa & ~(1 << bit));
                                }
                                _spiDriver.WriteGPA(_gpa);
                                return "okay\n";
                            }
                        }
                        return "_problem invalid bit";
                    case "reset_all":
                        SetMotor(0, _motorState[0] = MOTOR_STATE.STOPPED);
                        SetMotor(1, _motorState[1] = MOTOR_STATE.STOPPED);
                        return "okay\n";
                    case "crossdomain.xml":
                        return String.Format("<cross-domain-policy><allow-access-from domain=\"*\" to-ports = \"{0}\"/></cross-domain-policy>\n", port);
                    // set the motor direction
                    case "md":
                        {
                            int motor_id;
                            if (Int32.TryParse(parameters[0], out motor_id))
                            {
                                if ((motor_id == 0) || (motor_id == 1))
                                {
                                    switch(parameters[1])
                                    {
                                        default:
                                        case "Stopped":
                                            _motorState[motor_id] = MOTOR_STATE.STOPPED;
                                            break;
                                        case "Forward":
                                            _motorState[motor_id] = MOTOR_STATE.FORWARD;
                                            break;
                                        case "Reverse":
                                            _motorState[motor_id] = MOTOR_STATE.REVERSE;
                                            break;
                                    }
                                    return "okay\n";
                                }
                            }
                            return "_problem: invalid motor id\n";
                        }
                    // set motor power, only 7 settings
                    case "mp":
                        {
                            int motor_id;
                            if (Int32.TryParse(parameters[0], out motor_id))
                            {
                                int power;
                                if (Int32.TryParse(parameters[1], out power) && ((motor_id == 0) || (motor_id == 1)))
                                {
                                    // Just trying to maximise the pulse frequency
                                    // the total period is _motorOnCount[motor_id] + _motorOffCount[motor_id]
                                    int actual_power = (int)(Math.Min(6, Math.Max(0, power)));
                                    switch (actual_power)
                                    {
                                        case 0:
                                            _motorOnCount[motor_id] = 0;
                                            _motorOffCount[motor_id] = 5;
                                            break;
                                        case 1:
                                            _motorOnCount[motor_id] = 1;
                                            _motorOffCount[motor_id] = 3;
                                            break;
                                        case 2:
                                            _motorOnCount[motor_id] = 1;
                                            _motorOffCount[motor_id] = 2;
                                            break;
                                        case 3:
                                            _motorOnCount[motor_id] = 1;
                                            _motorOffCount[motor_id] = 1;
                                            break;
                                        case 4:
                                            _motorOnCount[motor_id] = 2;
                                            _motorOffCount[motor_id] = 1;
                                            break;
                                        case 5:
                                            _motorOnCount[motor_id] = 3;
                                            _motorOffCount[motor_id] = 1;
                                            break;
                                        case 6:
                                            _motorOnCount[motor_id] = 5;
                                            _motorOffCount[motor_id] = 0;
                                            break;
                                    }
                                    //await _gertbot.SetMotorPower(motor_id, power);
                                    return "okay\n";
                                }
                                else
                                {
                                    return "_problem: invalid motor id\n";
                                }
                            }
                            else
                            {
                                return "_problem: invalid motor id\n";
                            }
                        }
                    default:
                        return String.Format("_problem unknown command: {0}\n", request);
                }
            }
            catch (Exception ex)
            {
                return String.Format("_problem unknown command: {0}\n", request);
            }
        }

        // motor callback that drives the pulse width modulation to the PI Face board
        private void _motorTimerCallabck(object sender)
        {
            for (int i = 0; i < 2; i++)
            {
                int c = (_motorCount) % (_motorOffCount[i] + _motorOnCount[i]);
                if (c < _motorOffCount[i])
                {
                    SetMotor(i, MOTOR_STATE.STOPPED);
                }
                else
                {
                    SetMotor(i, _motorState[i]);
                }
            }
            _motorCount++;
        }

        // Set the PI Face bits to reflect the state of the motor
        private void SetMotor(int motor, MOTOR_STATE state)
        {
            switch (state)
            {
                default:
                case MOTOR_STATE.STOPPED:
                    SetBitState((byte)(MOTOR_BIT_FORWARD[motor] | MOTOR_BIT_BACK[motor]),0);
                    break;
                case MOTOR_STATE.FORWARD:
                    SetBitState(MOTOR_BIT_BACK[motor], MOTOR_BIT_FORWARD[motor]);
                    break;
                case MOTOR_STATE.REVERSE:
                    SetBitState(MOTOR_BIT_FORWARD[motor], MOTOR_BIT_BACK[motor]);
                    break;
            }
        }

        // actually set the PI Face board
        private void SetBitState(byte maskToSet, byte maskToClear)
        {
            // try to avoid turning on the both reverse and forward motor at the same time
            bool clear = false;
            lock (_gpaAObject)
            {
                if ((maskToClear & _gpa) != 0)
                {
                    _gpa = (byte)(_gpa & ~maskToClear);
                    clear = true;
                }
            }
            if (clear)
                _spiDriver.WriteGPA(_gpa);
            bool set = false;
            lock (_gpaAObject)
            {
                if ((maskToSet & _gpa) != maskToSet)
                {
                    _gpa = (byte)(_gpa | maskToSet);
                    set = true;
                }
            }
            if (set)
                _spiDriver.WriteGPA(_gpa);
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _timer.Dispose();
                    _motorTimer.Dispose();
                    _listener.Dispose();
                    _spiDriver.Dispose();
                }
                disposedValue = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
        }
        #endregion
    }
}
