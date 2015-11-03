/*
Copyright(c) 2015 Graham Chow

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace RaspberryPiScratchWebServer
{
    internal class GertbotEventArgs : EventArgs
    {
        public byte PinMask { get; set; }
        public byte PinState { get; set; }
    }

    internal delegate void GertbotChange(object sender, GertbotEventArgs e);

    public class GertbotUartDriver : IDisposable
    {
        private SerialDevice _serialDevice = null;
        private DataWriter _dw = null;
        private DataReader _dr = null;
        private const int BOARD_ID = 0;
        private const int PWM_VALUE = 5000;
        private const int DC_MOTOR_MODE = 1;

        internal event EventHandler<GertbotEventArgs> GertbotInterrupt;

        private async Task<bool> InitHardwarePrivate()
        {
            try
            {
                string aqs = SerialDevice.GetDeviceSelector();
                DeviceInformationCollection devices_info = await DeviceInformation.FindAllAsync(aqs);

                _serialDevice = await SerialDevice.FromIdAsync(devices_info[0].Id);
                if (_serialDevice != null)
                {

                    // Configure serial settings
                    _serialDevice.WriteTimeout = TimeSpan.FromMilliseconds(1000);
                    _serialDevice.ReadTimeout = TimeSpan.FromMilliseconds(1000);
                    _serialDevice.BaudRate = 57600;
                    _serialDevice.Parity = SerialParity.None;
                    _serialDevice.StopBits = SerialStopBitCount.One;
                    _serialDevice.DataBits = 8;

                    _dw = new DataWriter(_serialDevice.OutputStream);
                    _dr = new DataReader(_serialDevice.InputStream);
                    _dr.InputStreamOptions = InputStreamOptions.None;

                    int version = await GetBoardVersion();
                    int error;
                    await StopAll();
                    error = await GetErrorStatus();
                    if (error == 0)
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            await SetOperationMode(i);
                            if ((error = await GetErrorStatus()) != 0)
                                break;
                            await SetMotorPWM(i);
                            if ((error = await GetErrorStatus()) != 0)
                                break;
                            await SetMotorPower(i, 0);
                            if ((error = await GetErrorStatus()) != 0)
                                break;
                            await SetMotorDirection(i, 0);
                            if ((error = await GetErrorStatus()) != 0)
                                break;
                        }
                    }
                    if (error != 0)
                    {
                        _serialDevice.Dispose();
                        _serialDevice = null;
                    }
                }
                SendEvent();
                return (_serialDevice != null);
            }
            catch
            {
                _serialDevice = null;
                SendEvent();
                return false;
            }
        }

        public IAsyncOperation<bool> InitHardware()
        {
            return AsyncInfo.Run(_ =>
                Task.Run<bool>(async () =>
                {
                    return await InitHardwarePrivate();
                })
            );
        }

        public bool HardwareOk()
        {
            return _serialDevice != null;
        }

        private void SendEvent()
        {
            if (GertbotInterrupt != null)
            {
                GertbotEventArgs gbea = new GertbotEventArgs();
                GertbotInterrupt(this, gbea);
            }
        }

        private async Task<byte []> TransmitReceive(byte [] data, int responseLength)
        {
            Task<UInt32> storeAsyncTask;
            UInt32 bytesWritten;
            Task<UInt32> loadAsyncTask;
            try
            {
                await Task.Delay(10);
                _dw.WriteBytes(data);
                storeAsyncTask = _dw.StoreAsync().AsTask();
                bytesWritten = await storeAsyncTask;

                if (responseLength > 0)
                {
                    loadAsyncTask = _dr.LoadAsync((uint)responseLength).AsTask();
                    UInt32 bytesRead = await loadAsyncTask;
                    byte[] resp = new byte[responseLength];
                    _dr.ReadBytes(resp);
                    return resp;
                }
                else
                    return new byte[0];
            }
            catch(Exception ex)
            {
                ex.ToString();
                return new byte[0];
            }
        }

        private async Task<int> GetBoardVersion()
        {
            byte[] request = new byte[7];
            request[0] = 0xA0;
            request[1] = 0x13;
            request[2] = BOARD_ID;
            request[3] = 0x50;
            request[4] = 0x50;
            request[5] = 0x50;
            request[6] = 0x50;
            byte[] resp = await TransmitReceive(request, 4);
            int version = resp[2] << 8 + resp[3];
            return version;
        }

        private async Task<bool> SetOperationMode(int motorId)
        {
            byte[] request = new byte[5];
            request[0] = 0xA0;
            request[1] = 0x01;
            request[2] = (byte)motorId;
            request[3] = DC_MOTOR_MODE;
            request[4] = 0x50;
            await TransmitReceive(request, 0);
            return true;
        }

        private async Task SetMotorPWM(int motorId)
        {
            byte[] request = new byte[6];
            request[0] = 0xA0;
            request[1] = 0x04;
            request[2] = (byte)motorId;
            request[3] = (PWM_VALUE >> 8) & 0xFF;
            request[4] = (PWM_VALUE) & 0xFF;
            request[5] = 0x50;
            await TransmitReceive(request, 0);
        }

        public async Task SetMotorPower(int motorId, int power)
        {
            int p = Math.Max(0, Math.Min(1000, (power * 10)));
            byte[] request = new byte[6];
            request[0] = 0xA0;
            request[1] = 0x05;
            request[2] = (byte)motorId;
            request[3] = (byte)((p >> 8) & 0xFF);
            request[4] = (byte)(p & 0xFF);
            request[5] = 0x50;
            await TransmitReceive(request, 0);
        }

        public async Task SetMotorDirection(int motorId, int direction)
        {
            if ((direction != 1) && (direction != 2))
                direction = 0;
            byte[] request = new byte[5];
            request[0] = 0xA0;
            request[1] = 0x06;
            request[2] = (byte)motorId;
            request[3] = (byte)direction;
            request[4] = 0x50;

            await TransmitReceive(request, 0);
        }

        public async Task StopAll()
        {
            byte[] request = new byte[4];
            request[0] = 0xA0;
            request[1] = 0x0A;
            request[2] = 0x81;
            request[3] = 0x50;
            await TransmitReceive(request, 0);
        }

        internal struct MotorConfig
        {
            public int mode;
            public int frequency;
            public int duty_cycle;
        };

        private async Task<MotorConfig> GetMotorConfig(int motorId)
        {
            byte[] request = new byte[19];
            request[0] = 0xA0;
            request[1] = 0x1B;
            request[2] = (byte)motorId;
            for(int i=3;i<3+13;i++)
                request[i] = 0x50;
            byte[] resp = await TransmitReceive(request, 13);

            MotorConfig mc;
            mc.mode = resp[2] & 0x1F;
            mc.frequency = (resp[4] << 16) + (resp[5] << 8) + resp[6];
            mc.duty_cycle = (resp[7] << 8) + resp[8];
            return mc;
        }

        private async Task<int> GetErrorStatus()
        {
            byte[] request = new byte[7];
            request[0] = 0xA0;
            request[1] = 0x07;
            request[2] = BOARD_ID;
            request[3] = 0x50;
            request[4] = 0x50;
            request[5] = 0x50;
            request[6] = 0x50;
            byte[] resp = await TransmitReceive(request, 4);
            int error = resp[2] << 8 + resp[3];
            return error;
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    if (_dr != null)
                        _dr.Dispose();
                    if (_dw != null)
                        _dw.Dispose();
                    if (_serialDevice != null)
                        _serialDevice.Dispose();
                }
                disposedValue = true;
            }
        }

        ~GertbotUartDriver()
        {
           // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
           Dispose(false);
        }

        // This code added to correctly implement the disposable pattern.
        void IDisposable.Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion


    }
}
