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
using Windows.Devices.Enumeration;
using Windows.Devices.Spi;
using Windows.Devices.Gpio;
using Windows.Graphics;
using Windows.Foundation;
using Windows.System.Threading;
using System.Threading;
using System.Runtime.InteropServices.WindowsRuntime;


namespace RaspberryPiScratchWebServer
{

    internal class PiFaceEventArgs : EventArgs
    {
        public byte PinMask { get; set; }
        public byte PinState { get; set; }
    }

    internal delegate void PiFaceInputChange(object sender, PiFaceEventArgs e);

    internal sealed class PiFaceSpiDriver : IDisposable
    {
        // notes reset on the MCP23S17 is tied to 3.3V
        // Block GPA is configured as inputs
        // Block GPB is configured as outputs
        // leave the IOCON.BANK = 0

        private GpioController IoController = null;
        private GpioPin InterruptPin = null;

        private const string SPI_CONTROLLER_NAME = "SPI0";  /* For Raspberry Pi 2, use SPI0                             */
        private const Int32 SPI_CHIP_SELECT_LINE = 0;       /* Line 0 maps to physical pin number 24 on the Rpi2        */
        private const Int32 INTERRUPT_PIN = 25;

        private const byte SPI_SLAVE_READ = 1;
        private const byte SPI_SLAVE_WRITE = 0;
        private const byte SPI_SLAVE_ID = 0x40;
        private const byte SPI_SLAVE_ADDR = 0; // 0, 1, 2

        private const byte IOCONA = 0xA;
        private const byte IODIRA = 0x00;
        private const byte IODIRB = 0x01;
        private const byte GPPUA = 0x0C;
        private const byte GPPUB = 0x0D;
        private const byte GPIOA = 0x12;
        private const byte GPIOB = 0x13;
        private const byte OLATA = 0x14;
        private const byte OLATB = 0x15;

        private const byte DEFVALB = 0x7;
        private const byte INTCONB = 0xB;
        private const byte GPINTENB = 0x5;
        private const byte INTFB = 0xF;
        private const byte INTCAPB = 0x11;

        private SpiDevice SpiDev = null;

        internal event EventHandler<PiFaceEventArgs> PiFaceInterrupt;

        private async Task<bool> InitHardwarePrivate()
        {
            try
            {
                IoController = GpioController.GetDefault();
                InterruptPin = IoController.OpenPin(INTERRUPT_PIN);
                InterruptPin.DebounceTimeout = new TimeSpan(0, 0, 0, 0, 50);
                InterruptPin.Write(GpioPinValue.High);
                InterruptPin.SetDriveMode(GpioPinDriveMode.Input);
                InterruptPin.ValueChanged += Interrupt;

                var settings = new SpiConnectionSettings(SPI_CHIP_SELECT_LINE);
                settings.ClockFrequency = 10000000;      // max frequency for MCSP23S17 is 10Mhz
                settings.Mode = SpiMode.Mode3;          // data read on the rising edge - idle high
                string spiAqs = SpiDevice.GetDeviceSelector(SPI_CONTROLLER_NAME);
                DeviceInformationCollection devices_info = await DeviceInformation.FindAllAsync(spiAqs);
                await Task.Delay(20);

                SpiDev = await SpiDevice.FromIdAsync(devices_info[0].Id, settings);
                Write(IOCONA, 0x28); // BANK=0, SEQOP=1, HAEN=1 (Enable Addressing) interrupt not configured
                Write(IODIRA, 0x00); // GPIOA As Output
                Write(IODIRB, 0xFF); // GPIOB As Input
                Write(GPPUB, 0xFF); // configure pull ups

                Write(DEFVALB, 0x00); // normally high, only applicable if INTCONB == 0x0xFF
                Write(INTCONB, 0x00); // interrupt occurs upon pin change
                Write(GPINTENB, 0x00); // disable all interrupts
                Write(GPIOA, 0); // drive all outputs low

                // test read
                ReadGPA();
                SendEvent();
                return true;
            }
            catch(Exception ex)
            {
                SpiDev = null;
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
            return SpiDev != null;
        }

        private void Write(byte address, byte data)
        {
            if (SpiDev != null)
            {
                byte[] buffer = new byte[3];
                buffer[0] = SPI_SLAVE_ID | ((SPI_SLAVE_ADDR << 1) & 0x0E) | SPI_SLAVE_WRITE;
                buffer[1] = address;
                buffer[2] = data;
                SpiDev.Write(buffer);
            }
        }

        private byte Read(byte address)
        {
            if (SpiDev != null)
            {
                byte[] buffer = new byte[2];
                buffer[0] = SPI_SLAVE_ID | ((SPI_SLAVE_ADDR << 1) & 0x0E) | SPI_SLAVE_READ;
                buffer[1] = address;
                byte[] read_buffer = new byte[1];
                SpiDev.TransferSequential(buffer, read_buffer);
                return read_buffer[0];
            }
            return 0;
        }

        public void WriteGPA(byte value)
        {
            Write(GPIOA, value);
        }

        public byte ReadGPA()
        {
            return Read(OLATA);
        }

        public byte ReadGPB()
        {
            return Read(GPIOB);
        }

        private void SendEvent()
        {
            if (PiFaceInterrupt != null)
            {
                PiFaceEventArgs pfea = new PiFaceEventArgs();
                pfea.PinMask = Read(INTFB); // find out what caused the interrupt
                pfea.PinState = Read(INTCAPB); // read the state of the pins
                PiFaceInterrupt(this, pfea);
            }
        }

        private void Interrupt(GpioPin sender, GpioPinValueChangedEventArgs args)
        {
            SendEvent();
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if(SpiDev != null)
                    SpiDev.Dispose();
                if(InterruptPin != null)
                    InterruptPin.Dispose();
                disposedValue = true;
            }
        }

        ~PiFaceSpiDriver()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(false);
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
