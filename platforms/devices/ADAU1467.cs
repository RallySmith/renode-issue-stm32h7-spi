// Simple model to provide Analog Devices ADAU1467 basic SPI support
//
// Based on ADAU146[37] datasheet RevA

using System;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Config;

// Variant      DataMem ProgMem
// ADAU1463     48      16
// ADAU1467     80      24

// NOTE: Since this model does not emulate the operation of the DSP we
// do *not* provide an implementation of the 7k (16-bit) word ROM
// table. Especially since it is NOT accessible from the I2C or SPI
// interfaces.

namespace Antmicro.Renode.Peripherals.SPI
{
    public class ADAU1467 : ISPIPeripheral, IGPIOReceiver, IProvidesRegisterCollection<WordRegisterCollection>
    {
        public ADAU1467(string variant = "ADAU1467")
        {
            this.Variant = variant;

            this.memDM0P1 = new uint[20480];
            this.memDM0P2 = new uint[20480];
            this.memDM1P1 = new uint[20480];
            this.memDM1P2 = new uint[20480];
            this.memProgramP1 = new uint[12288];
            this.memProgramP2 = new uint[12288];

            RegistersCollection = CreateRegisters();
            Reset();
        }

        public string Variant { get; }

        // #0 as chip-select
        // #31 as nRESET (#24 active LOW : reset triggered on HIGH->LOW transition : sets all RAM and Registers to default values)
        public void OnGPIO(int number, bool value)
        {
            this.Log(LogLevel.Debug, "OnGPIO: number {0} value {1}", number, value);

            if ((0 == number) && value)
            {
                this.Log(LogLevel.Noisy, "Chip Select is deasserted");
                FinishTransmission();
            }

            if ((31 == number) && !value)
            {
                Reset();
            }
        }

        public void FinishTransmission()
        {
            // Set back to ready for next operation (which we assume
            // is done with the chip-selected)
            fsm = FSM.ChipAddress;
            //this.Log(LogLevel.Debug, "FinishTransmission: fsm {0}", fsm);
        }

        public void Reset()
        {
            // As per ADAU1467 datasheet RevA the state of the RAM is
            // **not** guaranteed to be cleared after a reset. So we
            // do not need to zero-down the memDM0, memDM1 or
            // memProgram areas.
            RegistersCollection.Reset();
            FinishTransmission();
        }

        public byte Transmit(byte data)
        {
            byte value = 0; // default for TX operations

            // CONSIDER:: if default access state (I2C) then track 3 chip-select pulses (Transmit of 1-byte (write) 0x00) to select SPI mode

            //this.Log(LogLevel.Debug, "Transmit: fsm {0} address 0x{1:X}", fsm, address);
            switch (fsm)
            {
            case FSM.ChipAddress:
                // Byte0 ChipAddress[7:1] Read_nWrite[0] where 1==Read, 0==Write
                if (0 != (data & 0xFE))
                {
                    this.Log(LogLevel.Warning, "Unexpected ChipAddress 0x{0:X}", data);
                }
                doRead = (0 != (data & (1 << 0)));
                fsm = FSM.SubAddressHigh;
                break;
            case FSM.SubAddressHigh:
                // Byte1 SubAddress[15:8]
                address &= (ushort)0x00FF;
                address |= (ushort)(data << 8);
                fsm = FSM.SubAddressLow;
                break;
            case FSM.SubAddressLow:
                // Byte2 SubAddress[7:0]
                address &= (ushort)0xFF00;
                address |= (ushort)data;
                // Based on RevD of the datasheet we treat all memory
                // below the control registers as 32-bits, and all of
                // the control registers as 16-bits
                if (address < 0xF000)
                {
                    isShort = false;
                }
                else
                {
                    isShort = true;
                }
                fsm = FSM.Data0;
                break;
            case FSM.Data0: // Big-endian data
                if (doRead)
                {
                    if (isShort)
                    {
                        latchedRead = ((uint)ReadControl(address) << 16);
                    }
                    else
                    {
                        latchedRead = ReadMemory(address);
                    }
                    value = (byte)((latchedRead >> 24) & 0xFF);
                    address++;
                }
                else
                {
                    // first byte
                    latchedWrite = (uint)data;
                }
                fsm = FSM.Data1;
                break;
            case FSM.Data1:
                if (doRead)
                {
                    value = (byte)((latchedRead >> 16) & 0xFF);
                }
                else
                {
                    latchedWrite = ((latchedWrite << 8)| (uint)data);
                }
                if (isShort)
                {
                    if (!doRead)
                    {
                        WriteControl(address, (ushort)latchedWrite);
                    }
                    address++;
                    fsm = FSM.Data0;
                }
                else
                {
                    fsm = FSM.Data2;
                }
                break;
            case FSM.Data2:
                if (doRead)
                {
                    value = (byte)((latchedRead >> 8) & 0xFF);
                }
                else
                {
                    latchedWrite = ((latchedWrite << 8)| (uint)data);
                }
                fsm = FSM.Data3;
                break;
            case FSM.Data3:
                if (doRead)
                {
                    value = (byte)(latchedRead & 0xFF);
                }
                else
                {
                    latchedWrite = ((latchedWrite << 8)| (uint)data);
                    WriteMemory(address, latchedWrite);
                    address++;
                }
                fsm = FSM.Data0;
                break;
            }

            return value;
        }

        private WordRegisterCollection CreateRegisters()
        {
            // NOTE: For the moment this mode does not simulate the
            // actual ADAU1467 hardware, but just provides "storage"
            // for the memory and control registers; hence the use of
            // the dummy "UNIMPLEMENTED" fields in the majority of the
            // registers below. The actual register fields can be
            // defined if a level of simulation is required.
            registersMap = new Dictionary<long, WordRegister>
            {
                { (long)Registers.PLL_CTRL0, new WordRegister(this, 0x0060) // PLL feedback divider
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PLL_CTRL1, new WordRegister(this, 0x0000) // PLL prescale divider
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PLL_CLK_SRC, new WordRegister(this, 0x0000) // PLL clock source
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PLL_ENABLE, new WordRegister(this, 0x0000) // PLL enable
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PLL_LOCK, new WordRegister(this,   0x0000) // PLL lock
                  .WithFlag(0, FieldMode.Read, name: "PLL_LOCK")
                  .WithReservedBits(1, 15)
                },
                { (long)Registers.MCLK_OUT, new WordRegister(this, 0x0000) // CLKOUT control
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PLL_WATCHDOG, new WordRegister(this, 0x0001) // Analog PLL watchdog control
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.CLK_GEN1_M, new WordRegister(this, 0x0006) // Denominator (M) for Clock Generator 1
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.CLK_GEN1_N, new WordRegister(this, 0x0001) // Numerator (N) for Clock Generator 1
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.CLK_GEN2_M, new WordRegister(this, 0x0009) // Denominator (M) for Clock Generator 2
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.CLK_GEN2_N, new WordRegister(this, 0x0001) // Numerator (N) for Clock Generator 2
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.CLK_GEN3_M, new WordRegister(this, 0x0000) // Denominator (M) for Clock Generator 3
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.CLK_GEN3_N, new WordRegister(this, 0x0000) // Numerator for (N) Clock Generator 3
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.CLK_GEN3_SRC, new WordRegister(this, 0x000E) // Input Reference for Clock Generator 3
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.CLK_GEN3_LOCK, new WordRegister(this, 0x0000) // Lock Bit for Clock Generator 3 input reference
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.POWER_ENABLE0, new WordRegister(this, 0x0000) // Power Enable 0
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.POWER_ENABLE1, new WordRegister(this, 0x0000) // Power Enable 1
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_INPUT0, new WordRegister(this, 0x0000) // ASRC input selector (ASRC 0, Channel 0 and Channel 1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_INPUT1, new WordRegister(this, 0x0000) // ASRC input selector (ASRC 1, Channel 2 and Channel 3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_INPUT2, new WordRegister(this, 0x0000) // ASRC input selector (ASRC 2, Channel 4 and Channel 5)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_INPUT3, new WordRegister(this, 0x0000) // ASRC input selector (ASRC 3, Channel 6 and Channel 7)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_INPUT4, new WordRegister(this, 0x0000) // ASRC input selector (ASRC 4, Channel 8 and Channel 9)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_INPUT5, new WordRegister(this, 0x0000) // ASRC input selector (ASRC 5, Channel 10 and Channel 11)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_INPUT6, new WordRegister(this, 0x0000) // ASRC input selector (ASRC 6, Channel 12 and Channel 13)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_INPUT7, new WordRegister(this, 0x0000) // ASRC input selector (ASRC 7, Channel 14 and Channel 15)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_OUT_RATE0, new WordRegister(this, 0x0000) // ASRC output rate (ASRC 0, Channel 0 and Channel 1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_OUT_RATE1, new WordRegister(this, 0x0000) // ASRC output rate (ASRC 1, Channel 2 and Channel 3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_OUT_RATE2, new WordRegister(this, 0x0000) // ASRC output rate (ASRC 2, Channel 4 and Channel 5)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_OUT_RATE3, new WordRegister(this, 0x0000) // ASRC output rate (ASRC 3, Channel 6 and Channel 7)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_OUT_RATE4, new WordRegister(this, 0x0000) // ASRC output rate (ASRC 4, Channel 8 and Channel 9)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_OUT_RATE5, new WordRegister(this, 0x0000) // ASRC output rate (ASRC 5, Channel 10 and Channel 11)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_OUT_RATE6, new WordRegister(this, 0x0000) // ASRC output rate (ASRC 6, Channel 12 and Channel 13)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_OUT_RATE7, new WordRegister(this, 0x0000) // ASRC output rate (ASRC 7, Channel 14 and Channel 15)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE0, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 0 and Channel 1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE1, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 2 and Channel 3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE2, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 4 and Channel 5)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE3, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 6 and Channel 7)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE4, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 8 and Channel 9)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE5, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 10 and Channel 11)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE6, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 12 and Channel 13)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE7, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 14 and Channel 15)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE8, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 16 and Channel 17)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE9, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 18 and Channel 19)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE10, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 20 and Channel 21)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE11, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 22 and Channel 23)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE12, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 24 and Channel 25)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE13, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 26 and Channel 27)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE14, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 28 and Channel 29)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE15, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 30 and Channel 31)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE16, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 32 and Channel 33)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE17, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 34 and Channel 35)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE18, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 36 and Channel 37)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE19, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 38 and Channel 39)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE20, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 40 and Channel 41)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE21, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 42 and Channel 43)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE22, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 44 and Channel 45)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOUT_SOURCE23, new WordRegister(this, 0x0000) // Source of data for serial output ports (Channel 46 and Channel 47)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIFTX_INPUT, new WordRegister(this, 0x0000) // S/PDIF transmitter data selector
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SERIAL_BYTE_0_0, new WordRegister(this, 0x0000) // Serial Port Control 0 (SDATA_IN0)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SERIAL_BYTE_0_1, new WordRegister(this, 0x0002) // Serial Port Control 1 (SDATA_IN0)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SERIAL_BYTE_1_0, new WordRegister(this, 0x0000) // Serial Port Control 0 (SDATA_IN1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SERIAL_BYTE_1_1, new WordRegister(this, 0x0002) // Serial Port Control 1 (SDATA_IN1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SERIAL_BYTE_2_0, new WordRegister(this, 0x0000) // Serial Port Control 0 (SDATA_IN2)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SERIAL_BYTE_2_1, new WordRegister(this, 0x0002) // Serial Port Control 1 (SDATA_IN2)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SERIAL_BYTE_3_0, new WordRegister(this, 0x0000) // Serial Port Control 0 (SDATA_IN3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SERIAL_BYTE_3_1, new WordRegister(this, 0x0002) // Serial Port Control 1 (SDATA_IN3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SERIAL_BYTE_4_0, new WordRegister(this, 0x0000) // Serial Port Control 0 (SDATA_OUT0)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SERIAL_BYTE_4_1, new WordRegister(this, 0x0002) // Serial Port Control 1 (SDATA_OUT0)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SERIAL_BYTE_5_0, new WordRegister(this, 0x0000) // Serial Port Control 0 (SDATA_OUT1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SERIAL_BYTE_5_1, new WordRegister(this, 0x0002) // Serial Port Control 1 (SDATA_OUT1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SERIAL_BYTE_6_0, new WordRegister(this, 0x0000) // Serial Port Control 0 (SDATA_OUT2)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SERIAL_BYTE_6_1, new WordRegister(this, 0x0002) // Serial Port Control 1 (SDATA_OUT2)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SERIAL_BYTE_7_0, new WordRegister(this, 0x0000) // Serial Port Control 0 (SDATA_OUT3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SERIAL_BYTE_7_1, new WordRegister(this, 0x0002) // Serial Port Control 1 (SDATA_OUT3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATA_0_ROUTE, new WordRegister(this, 0x0000) // Serial Port Routing 0
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATA_1_ROUTE, new WordRegister(this, 0x0000) // Serial Port Routing 1
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATA_2_ROUTE, new WordRegister(this, 0x0000) // Serial Port Routing 2
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATA_3_ROUTE, new WordRegister(this, 0x0000) // Serial Port Routing 3
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATA_4_ROUTE, new WordRegister(this, 0x0000) // Serial Port Routing 4
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATA_5_ROUTE, new WordRegister(this, 0x0000) // Serial Port Routing 5
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATA_6_ROUTE, new WordRegister(this, 0x0000) // Serial Port Routing 6
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATA_7_ROUTE, new WordRegister(this, 0x0000) // Serial Port Routing 7
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN0, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 32, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN1, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 32, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN2, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 32, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN3, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 32, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN4, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 33, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN5, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 33, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN6, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 33, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN7, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 33, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN8, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 34, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN9, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 34, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN10, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 34, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN11, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 34, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN12, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 35, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN13, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 35, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN14, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 35, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN15, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 35, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN16, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 36, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN17, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 36, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN18, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 36, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN19, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 36, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN20, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 37, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN21, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 37, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN22, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 37, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN23, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 37, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN24, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 38, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN25, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 38, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN26, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 38, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN27, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 38, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN28, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 39, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN29, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 39, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN30, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 39, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN31, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 39, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN32, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 40, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN33, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 40, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN34, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 40, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN35, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 40, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN36, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 41, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN37, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 41, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN38, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 41, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN39, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 41, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN40, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 42, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN41, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 42, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN42, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 42, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN43, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 42, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN44, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 43, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN45, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 43, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN46, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 43, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN47, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 43, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN48, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 44, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN49, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 44, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN50, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 44, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN51, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 44, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN52, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 45, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN53, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 45, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN54, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 45, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN55, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 45, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN56, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 46, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN57, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 46, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN58, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 46, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN59, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 46, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN60, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 47, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN61, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 47, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN62, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 47, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_IN63, new WordRegister(this, 0x0000) // FTDM mapping for the serial inputs (Channel 47, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT0, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 0, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT1, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 0, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT2, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 0, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT3, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 0, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT4, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 1, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT5, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 1, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT6, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 1, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT7, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 1, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT8, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 2, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT9, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 2, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT10, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 2, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT11, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 2, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT12, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 3, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT13, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 3, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT14, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 3, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT15, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 3, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT16, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 4, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT17, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 4, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT18, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 4, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT19, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 4, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT20, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 5, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT21, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 5, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT22, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 5, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT23, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 5, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT24, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 6, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT25, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 6, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT26, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 6, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT27, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 6, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT28, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 7, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT29, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 7, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT30, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 7, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT31, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 2, Channel 7, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT32, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 0, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT33, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 0, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT34, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 0, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT35, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 0, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT36, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 1, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT37, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 1, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT38, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 1, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT39, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 1, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT40, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 2, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT41, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 2, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT42, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 2, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT43, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 2, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT44, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 3, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT45, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 3, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT46, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 3, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT47, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 3, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT48, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 4, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT49, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 4, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT50, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 4, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT51, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 4, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT52, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 5, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT53, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 5, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT54, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 5, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT55, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 5, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT56, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 6, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT57, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 6, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT58, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 6, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT59, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 6, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT60, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 7, Bits[31:24])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT61, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 7, Bits[23:16])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT62, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 7, Bits[15:8])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.FTDM_OUT63, new WordRegister(this, 0x0000) // FTDM mapping for the serial outputs (Port 3, Channel 7, Bits[7:0])
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.HIBERNATE, new WordRegister(this, 0x0000) // Hibernate setting
                  .WithFlag(0, name: "HIBERNATE")
                  .WithReservedBits(1, 15)
                },
                { (long)Registers.START_PULSE, new WordRegister(this, 0x0002) // Start pulse selection
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.START_CORE, new WordRegister(this, 0x0000) // Instruction to start the core
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.KILL_CORE, new WordRegister(this, 0x0000) // Instruction to stop the core
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.START_ADDRESS, new WordRegister(this, 0x0000) // Start address of the program
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.CORE_STATUS, new WordRegister(this, 0x0000) // Core status
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.PANIC_CLEAR, new WordRegister(this, 0x0000) // Clear the panic manager
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PANIC_PARITY_MASK, new WordRegister(this, 0x0003) // Panic parity
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PANIC_SOFTWARE_MASK, new WordRegister(this, 0x0000) // Panic Mask 0
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PANIC_WD_MASK, new WordRegister(this, 0x0000) // Panic Mask 1
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PANIC_STACK_MASK, new WordRegister(this, 0x0000) // Panic Mask 2
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PANIC_LOOP_MASK, new WordRegister(this, 0x0000) // Panic Mask 3
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PANIC_FLAG, new WordRegister(this, 0x0000) // Panic flag
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.PANIC_CODE, new WordRegister(this, 0x0000) // Panic code
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.EXECUTE_COUNT, new WordRegister(this, 0x0000) // Execute stage error program count
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.WATCHDOG_MAXCOUNT, new WordRegister(this, 0x0000) // Watchdog maximum count
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.WATCHDOG_PRESCALE, new WordRegister(this, 0x0000) // Watchdog prescale
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.BLOCKINT_EN, new WordRegister(this, 0x0000) // Enable block interrupts
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.BLOCKINT_VALUE, new WordRegister(this, 0x0000) // Value for the block interrupt counter
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PROG_CNTR0, new WordRegister(this, 0x0000) // Program counter, Bits[23:16]
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.PROG_CNTR1, new WordRegister(this, 0x0000) // Program counter, Bits[15:0]
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.PROG_CNTR_CLEAR, new WordRegister(this, 0x0000) // Program counter clear
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PROG_CNTR_LENGTH0, new WordRegister(this, 0x0000) // Program counter length, Bits[23:16]
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.PROG_CNTR_LENGTH1, new WordRegister(this, 0x0000) // Program counter length, Bits[15:0]
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.PROG_CNTR_MAXLENGTH0, new WordRegister(this, 0x0000) // Program counter max length, Bits[23:16]
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.PROG_CNTR_MAXLENGTH1, new WordRegister(this, 0x0000) // Program counter max length, Bits[15:0]
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.PANIC_PARITY_MASK1, new WordRegister(this, 0x0000) // Panic Mask Parity DM0 Bank[1:0]
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PANIC_PARITY_MASK2, new WordRegister(this, 0x0000) // Panic Mask Parity DM0 Bank[3:2]
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PANIC_PARITY_MASK3, new WordRegister(this, 0x0000) // Panic Mask Parity DM1 Bank[1:0]
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PANIC_PARITY_MASK4, new WordRegister(this, 0x0000) // Panic Mask Parity DM1 Bank[3:2]
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PANIC_PARITY_MASK5, new WordRegister(this, 0x0000) // Panic Mask Parity PM Bank[1:0]
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PANIC_CODE1, new WordRegister(this, 0x0000) // Panic Parity Error DM0 Bank[1:0]
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PANIC_CODE2, new WordRegister(this, 0x0000) // Panic Parity Error DM0 Bank[3:2]
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PANIC_CODE3, new WordRegister(this, 0x0000) // Panic Parity Error DM1 Bank[1:0]
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PANIC_CODE4, new WordRegister(this, 0x0000) // Panic Parity Error DM1 Bank[3:2]
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.PANIC_CODE5, new WordRegister(this, 0x0000) // Panic Parity Error PM Bank[1:0]
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP0_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (SS_M/MP0)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP1_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (MOSI_M/MP1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP2_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (SCL_M/SCLK_M/MP2)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP3_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (SDA_M/MISO_M/MP3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP4_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (LRCLK_OUT0/MP4)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP5_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (LRCLK_OUT1/MP5)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP6_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (MP6)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP7_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (MP7)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP8_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (LRCLK_OUT2/MP8)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP9_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (LRCLK_OUT3/MP9)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP10_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (LRCLK_IN0/MP10)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP11_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (LRCLK_IN1/MP11)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP12_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (LRCLK_IN2/MP12)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP13_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (LRCLK_IN3/MP13)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP0_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value (SS_M/MP0)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP1_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value (MOSI_M/MP1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP2_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value (SCL_M/SCLK_M/MP2)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP3_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value (SDA_M/MISO_M/MP3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP4_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value (LRCLK_OUT0/MP4)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP5_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value (LRCLK_OUT1/MP5)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP6_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value (MP6)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP7_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value (MP7)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP8_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value (LRCLK_OUT2/MP8)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP9_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value (LRCLK_OUT3/MP9)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP10_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value (LRCLK_IN0/MP10)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP11_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value (LRCLK_IN1/MP11)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP12_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value (LRCLK_IN2/MP12)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP13_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value (LRCLK_IN3/MP13)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP0_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value (SS_M/MP0)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP1_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value (MOSI_M/MP1)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP2_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value (SCL_M/SCLK_M/MP2)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP3_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value (SDA_M/MISO_M/MP3)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP4_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value (LRCLK_OUT0/MP4)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP5_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value (LRCLK_OUT1/MP5)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP6_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value (MP6)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP7_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value (MP7)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP8_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value (LRCLK_OUT2/MP8)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP9_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value (LRCLK_OUT3/MP9)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP10_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value (LRCLK_IN0/MP10)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP11_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value (LRCLK_IN1/MP11)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP12_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value (LRCLK_IN2/MP12)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP13_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value (LRCLK_IN3/MP13)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.DMIC_CTRL0, new WordRegister(this, 0x4000) // Digital PDM microphone control (Channel 0 and Channel 1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.DMIC_CTRL1, new WordRegister(this, 0x4000) // Digital PDM microphone control (Channel 2 and Channel 3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_LOCK, new WordRegister(this, 0x0000) // ASRC lock status
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.ASRC_MUTE, new WordRegister(this, 0x0000) // ASRC mute
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC0_RATIO, new WordRegister(this, 0x0000) // ASRC ratio (ASRC 0, Channel 0 and Channel 1)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.ASRC1_RATIO, new WordRegister(this, 0x0000) // ASRC ratio (ASRC 1, Channel 2 and Channel 3)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.ASRC2_RATIO, new WordRegister(this, 0x0000) // ASRC ratio (ASRC 2, Channel 4 and Channel 5)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.ASRC3_RATIO, new WordRegister(this, 0x0000) // ASRC ratio (ASRC 3, Channel 6 and Channel 7)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.ASRC4_RATIO, new WordRegister(this, 0x0000) // ASRC ratio (ASRC 4, Channel 8 and Channel 9)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.ASRC5_RATIO, new WordRegister(this, 0x0000) // ASRC ratio (ASRC 5, Channel 10 and Channel 11)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.ASRC6_RATIO, new WordRegister(this, 0x0000) // ASRC ratio (ASRC 6, Channel 12 and Channel 13)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.ASRC_RAMPMAX_OVR, new WordRegister(this, 0x07FF) // RAMPMAX override
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_RAMPMAX0, new WordRegister(this, 0x07FF) // ASRC0 RAMPMAX
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_RAMPMAX1, new WordRegister(this, 0x07FF) // ASRC1 RAMPMAX
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_RAMPMAX2, new WordRegister(this, 0x07FF) // ASRC2 RAMPMAX
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_RAMPMAX3, new WordRegister(this, 0x07FF) // ASRC3 RAMPMAX
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_RAMPMAX4, new WordRegister(this, 0x07FF) // ASRC4 RAMPMAX
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_RAMPMAX5, new WordRegister(this, 0x07FF) // ASRC5 RAMPMAX
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_RAMPMAX6, new WordRegister(this, 0x07FF) // ASRC6 RAMPMAX
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ASRC_RAMPMAX7, new WordRegister(this, 0x07FF) // ASRC7 RAMPMAX
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.ADC_READ0, new WordRegister(this, 0x0000) // Auxiliary ADC read value (AUXADC0)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.ADC_READ1, new WordRegister(this, 0x0000) // Auxiliary ADC read value (AUXADC1)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.ADC_READ2, new WordRegister(this, 0x0000) // Auxiliary ADC read value (AUXADC2)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.ADC_READ3, new WordRegister(this, 0x0000) // Auxiliary ADC read value (AUXADC3)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.ADC_READ4, new WordRegister(this, 0x0000) // Auxiliary ADC read value (AUXADC4)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.ADC_READ5, new WordRegister(this, 0x0000) // Auxiliary ADC read value (AUXADC5)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP14_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (MP14)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP15_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (MP15)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP16_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (SDATAIO0)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP17_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (SDATAIO1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP18_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (SDATAIO2)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP19_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (SDATAIO3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP20_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (SDATAIO4)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP21_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (SDATAIO5)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP22_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (SDATAIO6)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP23_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (SDATAIO7)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP24_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (SCL2_M)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP25_MODE, new WordRegister(this, 0x0000) // Multipurpose pin mode (SDA2_M)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP14_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP15_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP16_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP17_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP18_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP19_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP20_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP21_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP22_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP23_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP24_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP25_WRITE, new WordRegister(this, 0x0000) // Multipurpose pin write value
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP14_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP15_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP16_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP17_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP18_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP19_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP20_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP21_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP22_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP23_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP24_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.MP25_READ, new WordRegister(this, 0x0000) // Multipurpose pin read value
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SECONDARY_I2C, new WordRegister(this, 0x0000) // Secondary I2C
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_LOCK_DET, new WordRegister(this, 0x0000) // S/PDIF receiver lock bit detection
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CTRL, new WordRegister(this, 0x0000) // S/PDIF receiver control
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_RX_DECODE, new WordRegister(this, 0x0000) // Decoded signals from the S/PDIF receiver
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_COMPRMODE, new WordRegister(this, 0x0000) // Compression mode from the S/PDIF receiver
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RESTART, new WordRegister(this, 0x0000) // Automatically resume S/PDIF receiver audio input
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_LOSS_OF_LOCK, new WordRegister(this, 0x0000) // S/PDIF receiver loss of lock detection
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_MCLKSPEED, new WordRegister(this, 0x0001) // S/PDIF receiver MCLK speed
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_MCLKSPEED, new WordRegister(this, 0x0001) // S/PDIF transmitter MCLK speed
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_AUX_EN, new WordRegister(this, 0x0000) // S/PDIF receiver auxiliary outputs enable
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_RX_AUXBIT_READY, new WordRegister(this, 0x0000) // S/PDIF receiver auxiliary bits ready flag
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_LEFT_0, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_LEFT_1, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_LEFT_2, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_LEFT_3, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_LEFT_4, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_LEFT_5, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_LEFT_6, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_LEFT_7, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_LEFT_8, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_LEFT_9, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_LEFT_10, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_LEFT_11, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_RIGHT_0, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_RIGHT_1, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_RIGHT_2, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_RIGHT_3, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_RIGHT_4, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_RIGHT_5, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_RIGHT_6, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_RIGHT_7, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_RIGHT_8, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_RIGHT_9, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_RIGHT_10, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_CS_RIGHT_11, new WordRegister(this, 0x0000) // S/PDIF receiver channel status bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_LEFT_0, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_LEFT_1, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_LEFT_2, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_LEFT_3, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_LEFT_4, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_LEFT_5, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_LEFT_6, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_LEFT_7, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_LEFT_8, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_LEFT_9, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_LEFT_10, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_LEFT_11, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_RIGHT_0, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_RIGHT_1, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_RIGHT_2, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_RIGHT_3, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_RIGHT_4, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_RIGHT_5, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_RIGHT_6, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_RIGHT_7, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_RIGHT_8, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_RIGHT_9, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_RIGHT_10, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_UD_RIGHT_11, new WordRegister(this, 0x0000) // S/PDIF receiver user data bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_LEFT_0, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_LEFT_1, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_LEFT_2, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_LEFT_3, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_LEFT_4, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_LEFT_5, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_LEFT_6, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_LEFT_7, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_LEFT_8, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_LEFT_9, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_LEFT_10, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_LEFT_11, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_RIGHT_0, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_RIGHT_1, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_RIGHT_2, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_RIGHT_3, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_RIGHT_4, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_RIGHT_5, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_RIGHT_6, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_RIGHT_7, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_RIGHT_8, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_RIGHT_9, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_RIGHT_10, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_VB_RIGHT_11, new WordRegister(this, 0x0000) // S/PDIF receiver validity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_LEFT_0, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_LEFT_1, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_LEFT_2, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_LEFT_3, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_LEFT_4, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_LEFT_5, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_LEFT_6, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_LEFT_7, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_LEFT_8, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_LEFT_9, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_LEFT_10, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_LEFT_11, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (left)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_RIGHT_0, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_RIGHT_1, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_RIGHT_2, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_RIGHT_3, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_RIGHT_4, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_RIGHT_5, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_RIGHT_6, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_RIGHT_7, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_RIGHT_8, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_RIGHT_9, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_RIGHT_10, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_RX_PB_RIGHT_11, new WordRegister(this, 0x0000) // S/PDIF receiver parity bits (right)
                  .WithValueField(0, 16, FieldMode.Read, name: "UNIMPLEMENTED_RO")
                },
                { (long)Registers.SPDIF_TX_EN, new WordRegister(this, 0x0000) // S/PDIF transmitter enable
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CTRL, new WordRegister(this, 0x0000) // S/PDIF transmitter control
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_AUXBIT_SOURCE, new WordRegister(this, 0x0000) // S/PDIF transmitter auxiliary bits source select
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_LEFT_0, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_LEFT_1, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_LEFT_2, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_LEFT_3, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_LEFT_4, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_LEFT_5, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_LEFT_6, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_LEFT_7, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_LEFT_8, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_LEFT_9, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_LEFT_10, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_LEFT_11, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_RIGHT_0, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_RIGHT_1, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_RIGHT_2, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_RIGHT_3, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_RIGHT_4, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_RIGHT_5, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_RIGHT_6, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_RIGHT_7, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_RIGHT_8, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_RIGHT_9, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_RIGHT_10, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_CS_RIGHT_11, new WordRegister(this, 0x0000) // S/PDIF transmitter channel status bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_LEFT_0, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_LEFT_1, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_LEFT_2, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_LEFT_3, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_LEFT_4, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_LEFT_5, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_LEFT_6, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_LEFT_7, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_LEFT_8, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_LEFT_9, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_LEFT_10, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_LEFT_11, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_RIGHT_0, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_RIGHT_1, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_RIGHT_2, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_RIGHT_3, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_RIGHT_4, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_RIGHT_5, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_RIGHT_6, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_RIGHT_7, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_RIGHT_8, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_RIGHT_9, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_RIGHT_10, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_UD_RIGHT_11, new WordRegister(this, 0x0000) // S/PDIF transmitter user data bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_LEFT_0, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_LEFT_1, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_LEFT_2, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_LEFT_3, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_LEFT_4, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_LEFT_5, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_LEFT_6, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_LEFT_7, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_LEFT_8, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_LEFT_9, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_LEFT_10, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_LEFT_11, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_RIGHT_0, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_RIGHT_1, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_RIGHT_2, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_RIGHT_3, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_RIGHT_4, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_RIGHT_5, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_RIGHT_6, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_RIGHT_7, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_RIGHT_8, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_RIGHT_9, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_RIGHT_10, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_VB_RIGHT_11, new WordRegister(this, 0x0000) // S/PDIF transmitter validity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_LEFT_0, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_LEFT_1, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_LEFT_2, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_LEFT_3, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_LEFT_4, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_LEFT_5, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_LEFT_6, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_LEFT_7, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_LEFT_8, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_LEFT_9, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_LEFT_10, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_LEFT_11, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (left)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_RIGHT_0, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_RIGHT_1, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_RIGHT_2, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_RIGHT_3, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_RIGHT_4, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_RIGHT_5, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_RIGHT_6, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_RIGHT_7, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_RIGHT_8, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_RIGHT_9, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_RIGHT_10, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PB_RIGHT_11, new WordRegister(this, 0x0000) // S/PDIF transmitter parity bits (right)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.BCLK_IN0_PIN, new WordRegister(this, 0x0018) // BCLK input pins drive strength and slew rate (BCLK_IN0)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.BCLK_IN1_PIN, new WordRegister(this, 0x0018) // BCLK input pins drive strength and slew rate (BCLK_IN1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.BCLK_IN2_PIN, new WordRegister(this, 0x0018) // BCLK input pins drive strength and slew rate (BCLK_IN2)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.BCLK_IN3_PIN, new WordRegister(this, 0x0018) // BCLK input pins drive strength and slew rate (BCLK_IN3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.BCLK_OUT0_PIN, new WordRegister(this, 0x0018) // BCLK output pins drive strength and slew rate (BCLK_OUT0)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.BCLK_OUT1_PIN, new WordRegister(this, 0x0018) // BCLK output pins drive strength and slew rate (BCLK_OUT1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.BCLK_OUT2_PIN, new WordRegister(this, 0x0018) // BCLK output pins drive strength and slew rate (BCLK_OUT2)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.BCLK_OUT3_PIN, new WordRegister(this, 0x0018) // BCLK output pins drive strength and slew rate (BCLK_OUT3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.LRCLK_IN0_PIN, new WordRegister(this, 0x0018) // LRCLK input pins drive strength and slew rate (LRCLK_IN0)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.LRCLK_IN1_PIN, new WordRegister(this, 0x0018) // LRCLK input pins drive strength and slew rate (LRCLK_IN1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.LRCLK_IN2_PIN, new WordRegister(this, 0x0018) // LRCLK input pins drive strength and slew rate (LRCLK_IN2)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.LRCLK_IN3_PIN, new WordRegister(this, 0x0018) // LRCLK input pins drive strength and slew rate (LRCLK_IN3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.LRCLK_OUT0_PIN, new WordRegister(this, 0x0018) // LRCLK output pins drive strength and slew rate (LRCLK_OUT0)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.LRCLK_OUT1_PIN, new WordRegister(this, 0x0018) // LRCLK output pins drive strength and slew rate (LRCLK_OUT1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.LRCLK_OUT2_PIN, new WordRegister(this, 0x0018) // LRCLK output pins drive strength and slew rate (LRCLK_OUT2)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.LRCLK_OUT3_PIN, new WordRegister(this, 0x0018) // LRCLK output pins drive strength and slew rate (LRCLK_OUT3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATA_IN0_PIN, new WordRegister(this, 0x0018) // SDATA input pins drive strength and slew rate (SDATA_IN0)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATA_IN1_PIN, new WordRegister(this, 0x0018) // SDATA input pins drive strength and slew rate (SDATA_IN1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATA_IN2_PIN, new WordRegister(this, 0x0018) // SDATA input pins drive strength and slew rate (SDATA_IN2)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATA_IN3_PIN, new WordRegister(this, 0x0018) // SDATA input pins drive strength and slew rate (SDATA_IN3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATA_OUT0_PIN, new WordRegister(this, 0x0008) // SDATA output pins drive strength and slew rate (SDATA_OUT0)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATA_OUT1_PIN, new WordRegister(this, 0x0008) // SDATA output pins drive strength and slew rate (SDATA_OUT1)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATA_OUT2_PIN, new WordRegister(this, 0x0008) // SDATA output pins drive strength and slew rate (SDATA_OUT2)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATA_OUT3_PIN, new WordRegister(this, 0x0008) // SDATA output pins drive strength and slew rate (SDATA_OUT3)
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SPDIF_TX_PIN, new WordRegister(this, 0x0008) // S/PDIF transmitter pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SCLK_SCL_PIN, new WordRegister(this, 0x0008) // SCLK/SCL pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MISO_SDA_PIN, new WordRegister(this, 0x0008) // MISO/SDA pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SS_PIN, new WordRegister(this, 0x0018) // SS/ADDR0 pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MOSI_ADDR1_PIN, new WordRegister(this, 0x0018) // MOSI/ADDR1 pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SCLK_SCL_M_PIN, new WordRegister(this, 0x0008) // SCL_M/SCLK_M/MP2 pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MISO_SDA_M_PIN, new WordRegister(this, 0x0008) // SDA_M/MISO_M/MP3 pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SS_M_PIN, new WordRegister(this, 0x0018) // SS_M/MP0 pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MOSI_M_PIN, new WordRegister(this, 0x0018) // MOSI_M/MP1 pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP6_PIN, new WordRegister(this, 0x0018) // MP6 pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP7_PIN, new WordRegister(this, 0x0018) // MP7 pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.CLKOUT_PIN, new WordRegister(this, 0x0008) // CLKOUT pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP14_PIN, new WordRegister(this, 0x0018) // MP14 pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP15_PIN, new WordRegister(this, 0x0018) // MP15 pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATAIO0_PIN, new WordRegister(this, 0x0018) // SDATAIO0 IN/OUT pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATAIO1_PIN, new WordRegister(this, 0x0018) // SDATAIO1 IN/OUT pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATAIO2_PIN, new WordRegister(this, 0x0018) // SDATAIO2 IN/OUT pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATAIO3_PIN, new WordRegister(this, 0x0018) // SDATAIO3 IN/OUT pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATAIO4_PIN, new WordRegister(this, 0x0018) // SDATAIO4 IN/OUT pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATAIO5_PIN, new WordRegister(this, 0x0018) // SDATAIO5 IN/OUT pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATAIO6_PIN, new WordRegister(this, 0x0018) // SDATAIO6 IN/OUT pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SDATAIO7_PIN, new WordRegister(this, 0x0018) // SDATAIO7 IN/OUT pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP24_PIN, new WordRegister(this, 0x0008) // MP24 pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.MP25_PIN, new WordRegister(this, 0x0008) // MP25 pin drive strength and slew rate
                  .WithValueField(0, 16, name: "UNIMPLEMENTED")
                },
                { (long)Registers.SOFT_RESET, new WordRegister(this, 0x0001) // Soft reset RW
                  .WithFlag(0, name: "SOFT_RESET")
                  .WithReservedBits(1, 15)
                },
                { (long)Registers.SECONDPAGE_ENABLE, new WordRegister(this, 0x0000) // Second page control
                  .WithFlag(0, out pageSelect, name: "PAGE", changeCallback:(_, value) => { SelectPage(value); })
                  .WithReservedBits(1, 15)
                },
            };
            return new WordRegisterCollection(this, registersMap);
        }

        private void SelectPage(bool page)
        {
            // false=lower=Page1 true=upper=Page2
            this.Log(LogLevel.Debug, "SelectPage: page {0}", page);

            // We just use the pageSelect variable in the memory
            // access routines to select Page1 or Page2 so this is just a
            // diagnostic function currently.
        }

        private (uint[], ushort) MemoryIndex(ushort address, uint pageOverride = 0)
        {
            bool pSel;

            switch (pageOverride)
            {
            case 1: // Lower
                pSel = false;
                break;
            case 2: // Upper
                pSel = true;
                break;
            default: // Use run-time control register selection:
                pSel = pageSelect.Value;
                break;
            }

            ushort index = 0;
            uint[] memblk = null;
            if (address < 0x5000)
            {
                memblk = (pSel ? memDM0P2 : memDM0P1);
                index = address;
            }
            else if ((0x6000 <= address) && (address < 0xB000))
            {
                memblk = (pSel ? memDM1P2 : memDM1P1);
                index = (ushort)(address - 0x6000);
            }
            else if ((0xC000 <= address) && (address < 0xF000))
            {
                memblk = (pSel ? memProgramP2 : memProgramP1);
                index = (ushort)(address - 0xC000);
            }
            else
            {
                this.Log(LogLevel.Error, "INVALID mem addr 0x{0:X}", address);
            }

            return (memblk, index);
        }

        public ushort ReadControl(ushort address)
        {
            ushort value = RegistersCollection.Read(address);
            this.Log(LogLevel.Debug, "READ  ctl addr 0x{0:X} value 0x{1:X}", address, value);
            return value;
        }

        public uint ReadMemory(ushort address, uint pageOverride = 0)
        {
            uint value = 0x00000000;
            var maccess = MemoryIndex(address, pageOverride);
            if (null != maccess.Item1)
            {
                uint[] ma = maccess.Item1;
                ushort mi = maccess.Item2;
                value = ma[mi];
            }

            this.Log(LogLevel.Debug, "READ  mem addr 0x{0:X} value 0x{1:X}", address, value);
            return value;
        }

        public void WriteControl(ushort address, ushort value)
        {
            RegistersCollection.Write(address, value);
            this.Log(LogLevel.Debug, "WRITE ctl addr 0x{0:X} value 0x{1:X}", address, value);
        }

        public void WriteMemory(ushort address, uint value, uint pageOverride = 0)
        {
            this.Log(LogLevel.Debug, "WRITE mem addr 0x{0:X} value 0x{1:X}", address, value);
            var maccess = MemoryIndex(address, pageOverride);
            if (null != maccess.Item1)
            {
                uint[] ma = maccess.Item1;
                ushort mi = maccess.Item2;
                ma[mi] = value;
            }

            // As per the ADAU1467 datasheet Rev.A Table 55 we mimic
            // the "Software Safeload" approach. We trigger the
            // operation on the 0x6006 num_SafeLoad_Lower or the
            // 0x6007 num_SafeLoad_Upper  write.

            // Page1 // we expect 0x6007 to be zero
            if ((0x6006 == address) && (value >= 1))
            {
                ushort safeAddress = (ushort)memDM1P1[5]; // 0x6005
                var safeAccess = MemoryIndex(safeAddress);
                if (null != safeAccess.Item1)
                {
                    uint[] parameterAddress = safeAccess.Item1;
                    ushort parameterIndex = safeAccess.Item2;
                    this.Log(LogLevel.Debug, "WRITE Safeload:P1: address 0x{0:X}", safeAddress);
                    for (var dIndex = 0; (dIndex < value); dIndex++)
                    {
                        uint safeValue = memDM1P1[dIndex];
                        parameterAddress[parameterIndex + dIndex] = safeValue;
                        this.Log(LogLevel.Debug, "WRITE Safeload:P1: 0x{0:X} = 0x{1:X}", (safeAddress + dIndex), safeValue);
                    }
                }
            }

            // Page2 // we expect 0x6006 to be zero
            if ((0x6007 == address) && (value >= 1))
            {
                ushort safeAddress = (ushort)memDM1P2[5]; // 0x6005
                var safeAccess = MemoryIndex(safeAddress);
                if (null != safeAccess.Item1)
                {
                    uint[] parameterAddress = safeAccess.Item1;
                    ushort parameterIndex = safeAccess.Item2;
                    this.Log(LogLevel.Debug, "WRITE Safeload:P2: address 0x{0:X}", safeAddress);
                    for (var dIndex = 0; (dIndex < value); dIndex++)
                    {
                        uint safeValue = memDM1P2[dIndex];
                        parameterAddress[parameterIndex + dIndex] = safeValue;
                        this.Log(LogLevel.Debug, "WRITE Safeload:P2: 0x{0:X} = 0x{1:X}", (safeAddress + dIndex), safeValue);
                    }
                }
            }
        }

        public void SaveMemory(WriteFilePath fileName, ushort address, ushort numWords, bool upper = false)
        {
            uint pageOverride = (uint)(upper ? 2 : 1);
            this.Log(LogLevel.Debug, "SaveMemory P{2} address 0x{0:X} numWords {1}", address, numWords, pageOverride);
            try
            {
                // We use Create instead of OpenOrCreate since we are creating a new dump:
                using(var writer = new FileStream(fileName, FileMode.Create, FileAccess.Write))
                {
                    using(var bin = new BinaryWriter(writer, Encoding.UTF8, false))
                    {
                        while (0 != numWords)
                        {
                            uint value = ReadMemory(address, pageOverride);
                            bin.Write(value);
                            address++;
                            numWords--;
                        }
                    }

                    //writer.Flush(); // not needed since BinaryWriter should close
                }
            }
            catch(IOException e)
            {
                throw new RecoverableException(string.Format("Exception while writing file {0}: {1}", fileName, e.Message));
            }
        }

        // This is probably not the most efficient, but is simple:
        private class RegInfo
        {
            public ushort address { get; set; }
            public ushort value { get; set; }
        }

        // CONSIDER: Could pass a vector of ushort register addresses
        // to dump that set of registers instead of all the
        // registersMap entries.
        public void SaveRegisters(WriteFilePath fileName)
        {
            JsonArray regInfo = new JsonArray((int)registersMap.Count); // capacity

            foreach (var entry in registersMap)
            {
                RegInfo t1 = new RegInfo();
                t1.address = (ushort)entry.Key;
                t1.value = ReadControl(t1.address);
                regInfo.Add(t1);
            }

            string jsonString = SimpleJson.SerializeObject(regInfo);

            this.Log(LogLevel.Debug, "SaveRegisters: jsonString {0}", jsonString);

            try
            {
                using(var writer = new FileStream(fileName, FileMode.Create, FileAccess.Write))
                {
                    byte[] outData = new UTF8Encoding(true).GetBytes(jsonString);
                    writer.Write(outData, 0, outData.Length);
                    writer.Flush();
                }
            }
            catch(IOException e)
            {
                throw new RecoverableException(string.Format("Exception while writing file {0}: {1}", fileName, e.Message));
            }
        }

        public WordRegisterCollection RegistersCollection { get; }

        private IDictionary<long, WordRegister> registersMap;

        private enum FSM
        {
            ChipAddress, // Idle
            SubAddressHigh,
            SubAddressLow,
            Data0,
            Data1,
            Data2,
            Data3,
        }

        private FSM fsm;
        private bool doRead;
        private bool isShort;
        private ushort address;
        private uint latchedRead;
        private uint latchedWrite;

        private IFlagRegisterField pageSelect;

        // CoreStatus:
        //      000 Not running (default)
        //      001 Running normally
        //      010 Paused
        //      011 Sleep
        //      100 Stalled

        // ADAU1452 Memory Addresses:
        // 0x0000 -> 0x4FFF     20480 words     DM0 (Data Memory 0)     32-bits
        private uint[] memDM0P1;
        private uint[] memDM0P2;
        // 0x6000 -> 0xAFFF     20480 words     DM1 (Data Memory 1)     32-bits
        private uint[] memDM1P1;
        private uint[] memDM1P2;
        // 0xC000 -> 0xDFFF     12288 words     Program Memory          32-bits
        private uint[] memProgramP1;
        private uint[] memProgramP2;

        // Based on ADAU1452 RevD datasheet Table68:
        private enum Registers : ushort
        {
            PLL_CTRL0 = 0xF000,
            PLL_CTRL1 = 0xF001,
            PLL_CLK_SRC = 0xF002,
            PLL_ENABLE = 0xF003,
            PLL_LOCK = 0xF004,
            MCLK_OUT = 0xF005,
            PLL_WATCHDOG = 0xF006,
            CLK_GEN1_M = 0xF020,
            CLK_GEN1_N = 0xF021,
            CLK_GEN2_M = 0xF022,
            CLK_GEN2_N = 0xF023,
            CLK_GEN3_M = 0xF024,
            CLK_GEN3_N = 0xF025,
            CLK_GEN3_SRC = 0xF026,
            CLK_GEN3_LOCK = 0xF027,
            POWER_ENABLE0 = 0xF050,
            POWER_ENABLE1 = 0xF051,
            ASRC_INPUT0 = 0xF100,
            ASRC_INPUT1 = 0xF101,
            ASRC_INPUT2 = 0xF102,
            ASRC_INPUT3 = 0xF103,
            ASRC_INPUT4 = 0xF104,
            ASRC_INPUT5 = 0xF105,
            ASRC_INPUT6 = 0xF106,
            ASRC_INPUT7 = 0xF107,
            ASRC_OUT_RATE0 = 0xF140,
            ASRC_OUT_RATE1 = 0xF141,
            ASRC_OUT_RATE2 = 0xF142,
            ASRC_OUT_RATE3 = 0xF143,
            ASRC_OUT_RATE4 = 0xF144,
            ASRC_OUT_RATE5 = 0xF145,
            ASRC_OUT_RATE6 = 0xF146,
            ASRC_OUT_RATE7 = 0xF147,
            SOUT_SOURCE0 = 0xF180,
            SOUT_SOURCE1 = 0xF181,
            SOUT_SOURCE2 = 0xF182,
            SOUT_SOURCE3 = 0xF183,
            SOUT_SOURCE4 = 0xF184,
            SOUT_SOURCE5 = 0xF185,
            SOUT_SOURCE6 = 0xF186,
            SOUT_SOURCE7 = 0xF187,
            SOUT_SOURCE8 = 0xF188,
            SOUT_SOURCE9 = 0xF189,
            SOUT_SOURCE10 = 0xF18A,
            SOUT_SOURCE11 = 0xF18B,
            SOUT_SOURCE12 = 0xF18C,
            SOUT_SOURCE13 = 0xF18D,
            SOUT_SOURCE14 = 0xF18E,
            SOUT_SOURCE15 = 0xF18F,
            SOUT_SOURCE16 = 0xF190,
            SOUT_SOURCE17 = 0xF191,
            SOUT_SOURCE18 = 0xF192,
            SOUT_SOURCE19 = 0xF193,
            SOUT_SOURCE20 = 0xF194,
            SOUT_SOURCE21 = 0xF195,
            SOUT_SOURCE22 = 0xF196,
            SOUT_SOURCE23 = 0xF197,
            SPDIFTX_INPUT = 0xF1C0,
            SERIAL_BYTE_0_0 = 0xF200,
            SERIAL_BYTE_0_1 = 0xF201,
            SERIAL_BYTE_1_0 = 0xF204,
            SERIAL_BYTE_1_1 = 0xF205,
            SERIAL_BYTE_2_0 = 0xF208,
            SERIAL_BYTE_2_1 = 0xF209,
            SERIAL_BYTE_3_0 = 0xF20C,
            SERIAL_BYTE_3_1 = 0xF20D,
            SERIAL_BYTE_4_0 = 0xF210,
            SERIAL_BYTE_4_1 = 0xF211,
            SERIAL_BYTE_5_0 = 0xF214,
            SERIAL_BYTE_5_1 = 0xF215,
            SERIAL_BYTE_6_0 = 0xF218,
            SERIAL_BYTE_6_1 = 0xF219,
            SERIAL_BYTE_7_0 = 0xF21C,
            SERIAL_BYTE_7_1 = 0xF21D,
            SDATA_0_ROUTE = 0xF240,
            SDATA_1_ROUTE = 0xF241,
            SDATA_2_ROUTE = 0xF242,
            SDATA_3_ROUTE = 0xF243,
            SDATA_4_ROUTE = 0xF244,
            SDATA_5_ROUTE = 0xF245,
            SDATA_6_ROUTE = 0xF246,
            SDATA_7_ROUTE = 0xF247,
            FTDM_IN0 = 0xF300,
            FTDM_IN1 = 0xF301,
            FTDM_IN2 = 0xF302,
            FTDM_IN3 = 0xF303,
            FTDM_IN4 = 0xF304,
            FTDM_IN5 = 0xF305,
            FTDM_IN6 = 0xF306,
            FTDM_IN7 = 0xF307,
            FTDM_IN8 = 0xF308,
            FTDM_IN9 = 0xF309,
            FTDM_IN10 = 0xF30A,
            FTDM_IN11 = 0xF30B,
            FTDM_IN12 = 0xF30C,
            FTDM_IN13 = 0xF30D,
            FTDM_IN14 = 0xF30E,
            FTDM_IN15 = 0xF30F,
            FTDM_IN16 = 0xF310,
            FTDM_IN17 = 0xF311,
            FTDM_IN18 = 0xF312,
            FTDM_IN19 = 0xF313,
            FTDM_IN20 = 0xF314,
            FTDM_IN21 = 0xF315,
            FTDM_IN22 = 0xF316,
            FTDM_IN23 = 0xF317,
            FTDM_IN24 = 0xF318,
            FTDM_IN25 = 0xF319,
            FTDM_IN26 = 0xF31A,
            FTDM_IN27 = 0xF31B,
            FTDM_IN28 = 0xF31C,
            FTDM_IN29 = 0xF31D,
            FTDM_IN30 = 0xF31E,
            FTDM_IN31 = 0xF31F,
            FTDM_IN32 = 0xF320,
            FTDM_IN33 = 0xF321,
            FTDM_IN34 = 0xF322,
            FTDM_IN35 = 0xF323,
            FTDM_IN36 = 0xF324,
            FTDM_IN37 = 0xF325,
            FTDM_IN38 = 0xF326,
            FTDM_IN39 = 0xF327,
            FTDM_IN40 = 0xF328,
            FTDM_IN41 = 0xF329,
            FTDM_IN42 = 0xF32A,
            FTDM_IN43 = 0xF32B,
            FTDM_IN44 = 0xF32C,
            FTDM_IN45 = 0xF32D,
            FTDM_IN46 = 0xF32E,
            FTDM_IN47 = 0xF32F,
            FTDM_IN48 = 0xF330,
            FTDM_IN49 = 0xF331,
            FTDM_IN50 = 0xF332,
            FTDM_IN51 = 0xF333,
            FTDM_IN52 = 0xF334,
            FTDM_IN53 = 0xF335,
            FTDM_IN54 = 0xF336,
            FTDM_IN55 = 0xF337,
            FTDM_IN56 = 0xF338,
            FTDM_IN57 = 0xF339,
            FTDM_IN58 = 0xF33A,
            FTDM_IN59 = 0xF33B,
            FTDM_IN60 = 0xF33C,
            FTDM_IN61 = 0xF33D,
            FTDM_IN62 = 0xF33E,
            FTDM_IN63 = 0xF33F,
            FTDM_OUT0 = 0xF380,
            FTDM_OUT1 = 0xF381,
            FTDM_OUT2 = 0xF382,
            FTDM_OUT3 = 0xF383,
            FTDM_OUT4 = 0xF384,
            FTDM_OUT5 = 0xF385,
            FTDM_OUT6 = 0xF386,
            FTDM_OUT7 = 0xF387,
            FTDM_OUT8 = 0xF388,
            FTDM_OUT9 = 0xF389,
            FTDM_OUT10 = 0xF38A,
            FTDM_OUT11 = 0xF38B,
            FTDM_OUT12 = 0xF38C,
            FTDM_OUT13 = 0xF38D,
            FTDM_OUT14 = 0xF38E,
            FTDM_OUT15 = 0xF38F,
            FTDM_OUT16 = 0xF390,
            FTDM_OUT17 = 0xF391,
            FTDM_OUT18 = 0xF392,
            FTDM_OUT19 = 0xF393,
            FTDM_OUT20 = 0xF394,
            FTDM_OUT21 = 0xF395,
            FTDM_OUT22 = 0xF396,
            FTDM_OUT23 = 0xF397,
            FTDM_OUT24 = 0xF398,
            FTDM_OUT25 = 0xF399,
            FTDM_OUT26 = 0xF39A,
            FTDM_OUT27 = 0xF39B,
            FTDM_OUT28 = 0xF39C,
            FTDM_OUT29 = 0xF39D,
            FTDM_OUT30 = 0xF39E,
            FTDM_OUT31 = 0xF39F,
            FTDM_OUT32 = 0xF3A0,
            FTDM_OUT33 = 0xF3A1,
            FTDM_OUT34 = 0xF3A2,
            FTDM_OUT35 = 0xF3A3,
            FTDM_OUT36 = 0xF3A4,
            FTDM_OUT37 = 0xF3A5,
            FTDM_OUT38 = 0xF3A6,
            FTDM_OUT39 = 0xF3A7,
            FTDM_OUT40 = 0xF3A8,
            FTDM_OUT41 = 0xF3A9,
            FTDM_OUT42 = 0xF3AA,
            FTDM_OUT43 = 0xF3AB,
            FTDM_OUT44 = 0xF3AC,
            FTDM_OUT45 = 0xF3AD,
            FTDM_OUT46 = 0xF3AE,
            FTDM_OUT47 = 0xF3AF,
            FTDM_OUT48 = 0xF3B0,
            FTDM_OUT49 = 0xF3B1,
            FTDM_OUT50 = 0xF3B2,
            FTDM_OUT51 = 0xF3B3,
            FTDM_OUT52 = 0xF3B4,
            FTDM_OUT53 = 0xF3B5,
            FTDM_OUT54 = 0xF3B6,
            FTDM_OUT55 = 0xF3B7,
            FTDM_OUT56 = 0xF3B8,
            FTDM_OUT57 = 0xF3B9,
            FTDM_OUT58 = 0xF3BA,
            FTDM_OUT59 = 0xF3BB,
            FTDM_OUT60 = 0xF3BC,
            FTDM_OUT61 = 0xF3BD,
            FTDM_OUT62 = 0xF3BE,
            FTDM_OUT63 = 0xF3BF,
            HIBERNATE = 0xF400,
            START_PULSE = 0xF401,
            START_CORE = 0xF402,
            KILL_CORE = 0xF403,
            START_ADDRESS = 0xF404,
            CORE_STATUS = 0xF405,
            PANIC_CLEAR = 0xF421,
            PANIC_PARITY_MASK = 0xF422,
            PANIC_SOFTWARE_MASK = 0xF423,
            PANIC_WD_MASK = 0xF424,
            PANIC_STACK_MASK = 0xF425,
            PANIC_LOOP_MASK = 0xF426,
            PANIC_FLAG = 0xF427,
            PANIC_CODE = 0xF428,
            EXECUTE_COUNT = 0xF432,
            WATCHDOG_MAXCOUNT = 0xF443,
            WATCHDOG_PRESCALE = 0xF444,
            BLOCKINT_EN = 0xF450,
            BLOCKINT_VALUE = 0xF451,
            PROG_CNTR0 = 0xF460,
            PROG_CNTR1 = 0xF461,
            PROG_CNTR_CLEAR = 0xF462,
            PROG_CNTR_LENGTH0 = 0xF463,
            PROG_CNTR_LENGTH1 = 0xF464,
            PROG_CNTR_MAXLENGTH0 = 0xF465,
            PROG_CNTR_MAXLENGTH1 = 0xF466,
            PANIC_PARITY_MASK1 = 0xF467,
            PANIC_PARITY_MASK2 = 0xF468,
            PANIC_PARITY_MASK3 = 0xF469,
            PANIC_PARITY_MASK4 = 0xF46A,
            PANIC_PARITY_MASK5 = 0xF46B,
            PANIC_CODE1 = 0xF46C,
            PANIC_CODE2 = 0xF46D,
            PANIC_CODE3 = 0xF46E,
            PANIC_CODE4 = 0xF46F,
            PANIC_CODE5 = 0xF470,
            MP0_MODE = 0xF510,
            MP1_MODE = 0xF511,
            MP2_MODE = 0xF512,
            MP3_MODE = 0xF513,
            MP4_MODE = 0xF514,
            MP5_MODE = 0xF515,
            MP6_MODE = 0xF516,
            MP7_MODE = 0xF517,
            MP8_MODE = 0xF518,
            MP9_MODE = 0xF519,
            MP10_MODE = 0xF51A,
            MP11_MODE = 0xF51B,
            MP12_MODE = 0xF51C,
            MP13_MODE = 0xF51D,
            MP0_WRITE = 0xF520,
            MP1_WRITE = 0xF521,
            MP2_WRITE = 0xF522,
            MP3_WRITE = 0xF523,
            MP4_WRITE = 0xF524,
            MP5_WRITE = 0xF525,
            MP6_WRITE = 0xF526,
            MP7_WRITE = 0xF527,
            MP8_WRITE = 0xF528,
            MP9_WRITE = 0xF529,
            MP10_WRITE = 0xF52A,
            MP11_WRITE = 0xF52B,
            MP12_WRITE = 0xF52C,
            MP13_WRITE = 0xF52D,
            MP0_READ = 0xF530,
            MP1_READ = 0xF531,
            MP2_READ = 0xF532,
            MP3_READ = 0xF533,
            MP4_READ = 0xF534,
            MP5_READ = 0xF535,
            MP6_READ = 0xF536,
            MP7_READ = 0xF537,
            MP8_READ = 0xF538,
            MP9_READ = 0xF539,
            MP10_READ = 0xF53A,
            MP11_READ = 0xF53B,
            MP12_READ = 0xF53C,
            MP13_READ = 0xF53D,
            DMIC_CTRL0 = 0xF560,
            DMIC_CTRL1 = 0xF561,
            ASRC_LOCK = 0xF580,
            ASRC_MUTE = 0xF581,
            ASRC0_RATIO = 0xF582,
            ASRC1_RATIO = 0xF583,
            ASRC2_RATIO = 0xF584,
            ASRC3_RATIO = 0xF585,
            ASRC4_RATIO = 0xF586,
            ASRC5_RATIO = 0xF587,
            ASRC6_RATIO = 0xF588,
            ASRC7_RATIO = 0xF589,
            ASRC_RAMPMAX_OVR = 0xF590,
            ASRC_RAMPMAX0 = 0xF591,
            ASRC_RAMPMAX1 = 0xF592,
            ASRC_RAMPMAX2 = 0xF593,
            ASRC_RAMPMAX3 = 0xF594,
            ASRC_RAMPMAX4 = 0xF595,
            ASRC_RAMPMAX5 = 0xF596,
            ASRC_RAMPMAX6 = 0xF597,
            ASRC_RAMPMAX7 = 0xF598,
            ADC_READ0 = 0xF5A0,
            ADC_READ1 = 0xF5A1,
            ADC_READ2 = 0xF5A2,
            ADC_READ3 = 0xF5A3,
            ADC_READ4 = 0xF5A4,
            ADC_READ5 = 0xF5A5,
            MP14_MODE = 0xF5C0,
            MP15_MODE = 0xF5C1,
            MP16_MODE = 0xF5C2,
            MP17_MODE = 0xF5C3,
            MP18_MODE = 0xF5C4,
            MP19_MODE = 0xF5C5,
            MP20_MODE = 0xF5C6,
            MP21_MODE = 0xF5C7,
            MP22_MODE = 0xF5C8,
            MP23_MODE = 0xF5C9,
            MP24_MODE = 0xF5CA,
            MP25_MODE = 0xF5CB,
            MP14_WRITE = 0xF5D0,
            MP15_WRITE = 0xF5D1,
            MP16_WRITE = 0xF5D2,
            MP17_WRITE = 0xF5D3,
            MP18_WRITE = 0xF5D4,
            MP19_WRITE = 0xF5D5,
            MP20_WRITE = 0xF5D6,
            MP21_WRITE = 0xF5D7,
            MP22_WRITE = 0xF5D8,
            MP23_WRITE = 0xF5D9,
            MP24_WRITE = 0xF5DA,
            MP25_WRITE = 0xF5DB,
            MP14_READ = 0xF5E0,
            MP15_READ = 0xF5E1,
            MP16_READ = 0xF5E2,
            MP17_READ = 0xF5E3,
            MP18_READ = 0xF5E4,
            MP19_READ = 0xF5E5,
            MP20_READ = 0xF5E6,
            MP21_READ = 0xF5E7,
            MP22_READ = 0xF5E8,
            MP23_READ = 0xF5E9,
            MP24_READ = 0xF5EA,
            MP25_READ = 0xF5EB,
            SECONDARY_I2C = 0xF5F0,
            SPDIF_LOCK_DET = 0xF600,
            SPDIF_RX_CTRL = 0xF601,
            SPDIF_RX_DECODE = 0xF602,
            SPDIF_RX_COMPRMODE = 0xF603,
            SPDIF_RESTART = 0xF604,
            SPDIF_LOSS_OF_LOCK = 0xF605,
            SPDIF_RX_MCLKSPEED = 0xF606,
            SPDIF_TX_MCLKSPEED = 0xF607,
            SPDIF_AUX_EN = 0xF608,
            SPDIF_RX_AUXBIT_READY = 0xF60F,
            SPDIF_RX_CS_LEFT_0 = 0xF610,
            SPDIF_RX_CS_LEFT_1 = 0xF611,
            SPDIF_RX_CS_LEFT_2 = 0xF612,
            SPDIF_RX_CS_LEFT_3 = 0xF613,
            SPDIF_RX_CS_LEFT_4 = 0xF614,
            SPDIF_RX_CS_LEFT_5 = 0xF615,
            SPDIF_RX_CS_LEFT_6 = 0xF616,
            SPDIF_RX_CS_LEFT_7 = 0xF617,
            SPDIF_RX_CS_LEFT_8 = 0xF618,
            SPDIF_RX_CS_LEFT_9 = 0xF619,
            SPDIF_RX_CS_LEFT_10 = 0xF61A,
            SPDIF_RX_CS_LEFT_11 = 0xF61B,
            SPDIF_RX_CS_RIGHT_0 = 0xF620,
            SPDIF_RX_CS_RIGHT_1 = 0xF621,
            SPDIF_RX_CS_RIGHT_2 = 0xF622,
            SPDIF_RX_CS_RIGHT_3 = 0xF623,
            SPDIF_RX_CS_RIGHT_4 = 0xF624,
            SPDIF_RX_CS_RIGHT_5 = 0xF625,
            SPDIF_RX_CS_RIGHT_6 = 0xF626,
            SPDIF_RX_CS_RIGHT_7 = 0xF627,
            SPDIF_RX_CS_RIGHT_8 = 0xF628,
            SPDIF_RX_CS_RIGHT_9 = 0xF629,
            SPDIF_RX_CS_RIGHT_10 = 0xF62A,
            SPDIF_RX_CS_RIGHT_11 = 0xF62B,
            SPDIF_RX_UD_LEFT_0 = 0xF630,
            SPDIF_RX_UD_LEFT_1 = 0xF631,
            SPDIF_RX_UD_LEFT_2 = 0xF632,
            SPDIF_RX_UD_LEFT_3 = 0xF633,
            SPDIF_RX_UD_LEFT_4 = 0xF634,
            SPDIF_RX_UD_LEFT_5 = 0xF635,
            SPDIF_RX_UD_LEFT_6 = 0xF636,
            SPDIF_RX_UD_LEFT_7 = 0xF637,
            SPDIF_RX_UD_LEFT_8 = 0xF638,
            SPDIF_RX_UD_LEFT_9 = 0xF639,
            SPDIF_RX_UD_LEFT_10 = 0xF63A,
            SPDIF_RX_UD_LEFT_11 = 0xF63B,
            SPDIF_RX_UD_RIGHT_0 = 0xF640,
            SPDIF_RX_UD_RIGHT_1 = 0xF641,
            SPDIF_RX_UD_RIGHT_2 = 0xF642,
            SPDIF_RX_UD_RIGHT_3 = 0xF643,
            SPDIF_RX_UD_RIGHT_4 = 0xF644,
            SPDIF_RX_UD_RIGHT_5 = 0xF645,
            SPDIF_RX_UD_RIGHT_6 = 0xF646,
            SPDIF_RX_UD_RIGHT_7 = 0xF647,
            SPDIF_RX_UD_RIGHT_8 = 0xF648,
            SPDIF_RX_UD_RIGHT_9 = 0xF649,
            SPDIF_RX_UD_RIGHT_10 = 0xF64A,
            SPDIF_RX_UD_RIGHT_11 = 0xF64B,
            SPDIF_RX_VB_LEFT_0 = 0xF650,
            SPDIF_RX_VB_LEFT_1 = 0xF651,
            SPDIF_RX_VB_LEFT_2 = 0xF652,
            SPDIF_RX_VB_LEFT_3 = 0xF653,
            SPDIF_RX_VB_LEFT_4 = 0xF654,
            SPDIF_RX_VB_LEFT_5 = 0xF655,
            SPDIF_RX_VB_LEFT_6 = 0xF656,
            SPDIF_RX_VB_LEFT_7 = 0xF657,
            SPDIF_RX_VB_LEFT_8 = 0xF658,
            SPDIF_RX_VB_LEFT_9 = 0xF659,
            SPDIF_RX_VB_LEFT_10 = 0xF65A,
            SPDIF_RX_VB_LEFT_11 = 0xF65B,
            SPDIF_RX_VB_RIGHT_0 = 0xF660,
            SPDIF_RX_VB_RIGHT_1 = 0xF661,
            SPDIF_RX_VB_RIGHT_2 = 0xF662,
            SPDIF_RX_VB_RIGHT_3 = 0xF663,
            SPDIF_RX_VB_RIGHT_4 = 0xF664,
            SPDIF_RX_VB_RIGHT_5 = 0xF665,
            SPDIF_RX_VB_RIGHT_6 = 0xF666,
            SPDIF_RX_VB_RIGHT_7 = 0xF667,
            SPDIF_RX_VB_RIGHT_8 = 0xF668,
            SPDIF_RX_VB_RIGHT_9 = 0xF669,
            SPDIF_RX_VB_RIGHT_10 = 0xF66A,
            SPDIF_RX_VB_RIGHT_11 = 0xF66B,
            SPDIF_RX_PB_LEFT_0 = 0xF670,
            SPDIF_RX_PB_LEFT_1 = 0xF671,
            SPDIF_RX_PB_LEFT_2 = 0xF672,
            SPDIF_RX_PB_LEFT_3 = 0xF673,
            SPDIF_RX_PB_LEFT_4 = 0xF674,
            SPDIF_RX_PB_LEFT_5 = 0xF675,
            SPDIF_RX_PB_LEFT_6 = 0xF676,
            SPDIF_RX_PB_LEFT_7 = 0xF677,
            SPDIF_RX_PB_LEFT_8 = 0xF678,
            SPDIF_RX_PB_LEFT_9 = 0xF679,
            SPDIF_RX_PB_LEFT_10 = 0xF67A,
            SPDIF_RX_PB_LEFT_11 = 0xF67B,
            SPDIF_RX_PB_RIGHT_0 = 0xF680,
            SPDIF_RX_PB_RIGHT_1 = 0xF681,
            SPDIF_RX_PB_RIGHT_2 = 0xF682,
            SPDIF_RX_PB_RIGHT_3 = 0xF683,
            SPDIF_RX_PB_RIGHT_4 = 0xF684,
            SPDIF_RX_PB_RIGHT_5 = 0xF685,
            SPDIF_RX_PB_RIGHT_6 = 0xF686,
            SPDIF_RX_PB_RIGHT_7 = 0xF687,
            SPDIF_RX_PB_RIGHT_8 = 0xF688,
            SPDIF_RX_PB_RIGHT_9 = 0xF689,
            SPDIF_RX_PB_RIGHT_10 = 0xF68A,
            SPDIF_RX_PB_RIGHT_11 = 0xF68B,
            SPDIF_TX_EN = 0xF690,
            SPDIF_TX_CTRL = 0xF691,
            SPDIF_TX_AUXBIT_SOURCE = 0xF69F,
            SPDIF_TX_CS_LEFT_0 = 0xF6A0,
            SPDIF_TX_CS_LEFT_1 = 0xF6A1,
            SPDIF_TX_CS_LEFT_2 = 0xF6A2,
            SPDIF_TX_CS_LEFT_3 = 0xF6A3,
            SPDIF_TX_CS_LEFT_4 = 0xF6A4,
            SPDIF_TX_CS_LEFT_5 = 0xF6A5,
            SPDIF_TX_CS_LEFT_6 = 0xF6A6,
            SPDIF_TX_CS_LEFT_7 = 0xF6A7,
            SPDIF_TX_CS_LEFT_8 = 0xF6A8,
            SPDIF_TX_CS_LEFT_9 = 0xF6A9,
            SPDIF_TX_CS_LEFT_10 = 0xF6AA,
            SPDIF_TX_CS_LEFT_11 = 0xF6AB,
            SPDIF_TX_CS_RIGHT_0 = 0xF6B0,
            SPDIF_TX_CS_RIGHT_1 = 0xF6B1,
            SPDIF_TX_CS_RIGHT_2 = 0xF6B2,
            SPDIF_TX_CS_RIGHT_3 = 0xF6B3,
            SPDIF_TX_CS_RIGHT_4 = 0xF6B4,
            SPDIF_TX_CS_RIGHT_5 = 0xF6B5,
            SPDIF_TX_CS_RIGHT_6 = 0xF6B6,
            SPDIF_TX_CS_RIGHT_7 = 0xF6B7,
            SPDIF_TX_CS_RIGHT_8 = 0xF6B8,
            SPDIF_TX_CS_RIGHT_9 = 0xF6B9,
            SPDIF_TX_CS_RIGHT_10 = 0xF6BA,
            SPDIF_TX_CS_RIGHT_11 = 0xF6BB,
            SPDIF_TX_UD_LEFT_0 = 0xF6C0,
            SPDIF_TX_UD_LEFT_1 = 0xF6C1,
            SPDIF_TX_UD_LEFT_2 = 0xF6C2,
            SPDIF_TX_UD_LEFT_3 = 0xF6C3,
            SPDIF_TX_UD_LEFT_4 = 0xF6C4,
            SPDIF_TX_UD_LEFT_5 = 0xF6C5,
            SPDIF_TX_UD_LEFT_6 = 0xF6C6,
            SPDIF_TX_UD_LEFT_7 = 0xF6C7,
            SPDIF_TX_UD_LEFT_8 = 0xF6C8,
            SPDIF_TX_UD_LEFT_9 = 0xF6C9,
            SPDIF_TX_UD_LEFT_10 = 0xF6CA,
            SPDIF_TX_UD_LEFT_11 = 0xF6CB,
            SPDIF_TX_UD_RIGHT_0 = 0xF6D0,
            SPDIF_TX_UD_RIGHT_1 = 0xF6D1,
            SPDIF_TX_UD_RIGHT_2 = 0xF6D2,
            SPDIF_TX_UD_RIGHT_3 = 0xF6D3,
            SPDIF_TX_UD_RIGHT_4 = 0xF6D4,
            SPDIF_TX_UD_RIGHT_5 = 0xF6D5,
            SPDIF_TX_UD_RIGHT_6 = 0xF6D6,
            SPDIF_TX_UD_RIGHT_7 = 0xF6D7,
            SPDIF_TX_UD_RIGHT_8 = 0xF6D8,
            SPDIF_TX_UD_RIGHT_9 = 0xF6D9,
            SPDIF_TX_UD_RIGHT_10 = 0xF6DA,
            SPDIF_TX_UD_RIGHT_11 = 0xF6DB,
            SPDIF_TX_VB_LEFT_0 = 0xF6E0,
            SPDIF_TX_VB_LEFT_1 = 0xF6E1,
            SPDIF_TX_VB_LEFT_2 = 0xF6E2,
            SPDIF_TX_VB_LEFT_3 = 0xF6E3,
            SPDIF_TX_VB_LEFT_4 = 0xF6E4,
            SPDIF_TX_VB_LEFT_5 = 0xF6E5,
            SPDIF_TX_VB_LEFT_6 = 0xF6E6,
            SPDIF_TX_VB_LEFT_7 = 0xF6E7,
            SPDIF_TX_VB_LEFT_8 = 0xF6E8,
            SPDIF_TX_VB_LEFT_9 = 0xF6E9,
            SPDIF_TX_VB_LEFT_10 = 0xF6EA,
            SPDIF_TX_VB_LEFT_11 = 0xF6EB,
            SPDIF_TX_VB_RIGHT_0 = 0xF6F0,
            SPDIF_TX_VB_RIGHT_1 = 0xF6F1,
            SPDIF_TX_VB_RIGHT_2 = 0xF6F2,
            SPDIF_TX_VB_RIGHT_3 = 0xF6F3,
            SPDIF_TX_VB_RIGHT_4 = 0xF6F4,
            SPDIF_TX_VB_RIGHT_5 = 0xF6F5,
            SPDIF_TX_VB_RIGHT_6 = 0xF6F6,
            SPDIF_TX_VB_RIGHT_7 = 0xF6F7,
            SPDIF_TX_VB_RIGHT_8 = 0xF6F8,
            SPDIF_TX_VB_RIGHT_9 = 0xF6F9,
            SPDIF_TX_VB_RIGHT_10 = 0xF6FA,
            SPDIF_TX_VB_RIGHT_11 = 0xF6FB,
            SPDIF_TX_PB_LEFT_0 = 0xF700,
            SPDIF_TX_PB_LEFT_1 = 0xF701,
            SPDIF_TX_PB_LEFT_2 = 0xF702,
            SPDIF_TX_PB_LEFT_3 = 0xF703,
            SPDIF_TX_PB_LEFT_4 = 0xF704,
            SPDIF_TX_PB_LEFT_5 = 0xF705,
            SPDIF_TX_PB_LEFT_6 = 0xF706,
            SPDIF_TX_PB_LEFT_7 = 0xF707,
            SPDIF_TX_PB_LEFT_8 = 0xF708,
            SPDIF_TX_PB_LEFT_9 = 0xF709,
            SPDIF_TX_PB_LEFT_10 = 0xF70A,
            SPDIF_TX_PB_LEFT_11 = 0xF70B,
            SPDIF_TX_PB_RIGHT_0 = 0xF710,
            SPDIF_TX_PB_RIGHT_1 = 0xF711,
            SPDIF_TX_PB_RIGHT_2 = 0xF712,
            SPDIF_TX_PB_RIGHT_3 = 0xF713,
            SPDIF_TX_PB_RIGHT_4 = 0xF714,
            SPDIF_TX_PB_RIGHT_5 = 0xF715,
            SPDIF_TX_PB_RIGHT_6 = 0xF716,
            SPDIF_TX_PB_RIGHT_7 = 0xF717,
            SPDIF_TX_PB_RIGHT_8 = 0xF718,
            SPDIF_TX_PB_RIGHT_9 = 0xF719,
            SPDIF_TX_PB_RIGHT_10 = 0xF71A,
            SPDIF_TX_PB_RIGHT_11 = 0xF71B,
            BCLK_IN0_PIN = 0xF780,
            BCLK_IN1_PIN = 0xF781,
            BCLK_IN2_PIN = 0xF782,
            BCLK_IN3_PIN = 0xF783,
            BCLK_OUT0_PIN = 0xF784,
            BCLK_OUT1_PIN = 0xF785,
            BCLK_OUT2_PIN = 0xF786,
            BCLK_OUT3_PIN = 0xF787,
            LRCLK_IN0_PIN = 0xF788,
            LRCLK_IN1_PIN = 0xF789,
            LRCLK_IN2_PIN = 0xF78A,
            LRCLK_IN3_PIN = 0xF78B,
            LRCLK_OUT0_PIN = 0xF78C,
            LRCLK_OUT1_PIN = 0xF78D,
            LRCLK_OUT2_PIN = 0xF78E,
            LRCLK_OUT3_PIN = 0xF78F,
            SDATA_IN0_PIN = 0xF790,
            SDATA_IN1_PIN = 0xF791,
            SDATA_IN2_PIN = 0xF792,
            SDATA_IN3_PIN = 0xF793,
            SDATA_OUT0_PIN = 0xF794,
            SDATA_OUT1_PIN = 0xF795,
            SDATA_OUT2_PIN = 0xF796,
            SDATA_OUT3_PIN = 0xF797,
            SPDIF_TX_PIN = 0xF798,
            SCLK_SCL_PIN = 0xF799,
            MISO_SDA_PIN = 0xF79A,
            SS_PIN = 0xF79B,
            MOSI_ADDR1_PIN = 0xF79C,
            SCLK_SCL_M_PIN = 0xF79D,
            MISO_SDA_M_PIN = 0xF79E,
            SS_M_PIN = 0xF79F,
            MOSI_M_PIN = 0xF7A0,
            MP6_PIN = 0xF7A1,
            MP7_PIN = 0xF7A2,
            CLKOUT_PIN = 0xF7A3,
            MP14_PIN = 0xF7A8,
            MP15_PIN = 0xF7A9,
            SDATAIO0_PIN = 0xF7B0,
            SDATAIO1_PIN = 0xF7B1,
            SDATAIO2_PIN = 0xF7B2,
            SDATAIO3_PIN = 0xF7B3,
            SDATAIO4_PIN = 0xF7B4,
            SDATAIO5_PIN = 0xF7B5,
            SDATAIO6_PIN = 0xF7B6,
            SDATAIO7_PIN = 0xF7B7,
            MP24_PIN = 0xF7B8,
            MP25_PIN = 0xF7B9,
            SOFT_RESET = 0xF890,
            SECONDPAGE_ENABLE = 0xF899
        }
    }
}
