//
// Copyright (c) 2010-2022 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Core;
using System.Collections.Generic;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Migrant;

namespace Antmicro.Renode.Peripherals.UART
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public sealed class STM32F7_USART : UARTBase, IDoubleWordPeripheral, IKnownSize
    {
        public STM32F7_USART(Machine machine, uint frequency, bool lowPowerMode = false) : base(machine)
        {
            IRQ = new GPIO();
            RegistersCollection = new DoubleWordRegisterCollection(this);
            this.frequency = frequency;
            this.lowPowerMode = lowPowerMode;
            DefineRegisters();
        }

        public override void Reset()
        {
            base.Reset();
            RegistersCollection.Reset();
            IRQ.Unset();
        }

        public uint ReadDoubleWord(long offset)
        {
            return RegistersCollection.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            RegistersCollection.Write(offset, value);
        }

        public override uint BaudRate => BaudRateMultiplier * frequency / baudRateDivisor.Value;
        
        public override Bits StopBits
        {
            get
            {
                switch(stopBits.Value)
                {
                case 0:
                    return Bits.One;
                case 1:
                    return Bits.Half;
                case 2:
                    return Bits.Two;
                case 3:
                    return Bits.OneAndAHalf;
                default:
                    throw new InvalidOperationException("Should not reach here.");
                }
            }
        }

        public override Parity ParityBit
        {
            get
            {
                if(!parityControlEnabled.Value)
                {
                    return Parity.None;
                }
                return paritySelection.Value ? Parity.Odd : Parity.Even;
            }
        }

        public GPIO IRQ { get; }

        public long Size => 0x400;

        public DoubleWordRegisterCollection RegistersCollection { get; }

        protected override void CharWritten()
        {
            UpdateInterrupt();
        }

        protected override void QueueEmptied()
        {
            UpdateInterrupt();
        }

        protected override bool IsReceiveEnabled => receiveEnabled.Value && enabled.Value;

        private void DefineRegisters()
        {
            var cr1 = Registers.ControlRegister1.Define(RegistersCollection)
                .WithFlag(0, out enabled, name: "UE")
                .WithTaggedFlag("UESM", 1)
                .WithFlag(2, out receiveEnabled, name: "RE")
                .WithFlag(3, out transmitEnabled, name: "TE")
                .WithTaggedFlag("IDLEIE", 4)
                .WithFlag(5, out readRegisterNotEmptyInterruptEnabled, name: "RXNEIE")
                .WithFlag(6, out transferCompleteInterruptEnabled, name: "TCIE")
                .WithFlag(7, out transmitRegisterEmptyInterruptEnabled, name: "TXEIE")
                .WithFlag(8, name: "PEIE")
                .WithFlag(9, out paritySelection, name: "PS")
                .WithFlag(10, out parityControlEnabled, name: "PCE")
                .WithTaggedFlag("WAKE", 11)
                .WithTaggedFlag("MO", 12)
                .WithTaggedFlag("MME", 13)
                .WithTaggedFlag("CMIE", 14)
                .WithTag("DEDT", 16, 5)
                .WithTag("DEAT", 21, 5)
                .WithTaggedFlag("M1", 28)
                .WithReservedBits(29, 3)
                .WithWriteCallback((_, __) => UpdateInterrupt());

            var cr2 = Registers.ControlRegister2.Define(RegistersCollection)
                .WithReservedBits(0, 4)
                .WithTaggedFlag("ADDM7", 4)
                .WithReservedBits(7, 1)
                .WithValueField(12, 2, out stopBits)
                .WithTaggedFlag("SWAP", 15)
                .WithTaggedFlag("RXINV", 16)
                .WithTaggedFlag("TXINV", 17)
                .WithTaggedFlag("DATAINV", 18)
                .WithTaggedFlag("MSBFIRST", 19)
                .WithTag("ADD", 24, 8);

            var cr3 = Registers.ControlRegister3.Define(RegistersCollection)
                .WithTaggedFlag("EIE", 0)
                .WithTaggedFlag("HDSEL", 3)
                .WithTaggedFlag("DMAR", 6)
                .WithTaggedFlag("DMAT", 7)
                .WithTaggedFlag("RTSE", 8)
                .WithTaggedFlag("CTSE", 9)
                .WithTaggedFlag("CTSIE", 10)
                .WithTaggedFlag("OVRDIS", 12)
                .WithTaggedFlag("DDRE", 13)
                .WithTaggedFlag("DEM", 14)
                .WithTaggedFlag("DEP", 15)
                .WithReservedBits(16, 1)
                .WithTag("WUS", 20, 2)
                .WithTaggedFlag("WUFIE", 22)
                .WithTaggedFlag("UCESM", 23)
                .WithReservedBits(25, 7);

            if(lowPowerMode)
            {
                Registers.BaudRate.Define(RegistersCollection)
                    .WithValueField(0, 20, out baudRateDivisor, name: "BRR")
                    .WithReservedBits(20, 12);
            }
            else
            {
                Registers.BaudRate.Define(RegistersCollection)
                    .WithValueField(0, 16, out baudRateDivisor, name: "BRR")
                    .WithReservedBits(16, 16);
            }

            var request = Registers.Request.Define(RegistersCollection)
                .WithFlag(1, FieldMode.Write, name: "SBKRQ")
                .WithFlag(2, FieldMode.Write, name: "MMRQ")
                .WithFlag(3, FieldMode.Write, writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        ClearBuffer();
                    }
                }, name: "RXFRQ")
                .WithReservedBits(6, 26);

            var isr = Registers.InterruptAndStatus.Define(RegistersCollection, lowPowerMode ? 0xC0u : 0x200000C0u)
                .WithTaggedFlag("PE", 0)
                .WithTaggedFlag("FE", 1)
                .WithTaggedFlag("NF", 2)
                .WithTaggedFlag("ORE", 3)
                .WithTaggedFlag("IDLE", 4)
                .WithFlag(5, FieldMode.Read, valueProviderCallback: _ => (Count != 0), name: "RXNE")
                .WithFlag(6, out transferComplete, FieldMode.Read, name: "TC")
                .WithFlag(7, FieldMode.Read, name: "TXE", valueProviderCallback: _ => true)
                .WithTaggedFlag("CTSIF", 9)
                .WithTaggedFlag("CTS", 10)
                .WithReservedBits(13, 1)
                .WithTaggedFlag("BUSY", 16)
                .WithTaggedFlag("CMF", 17)
                .WithTaggedFlag("SBKF", 18)
                .WithTaggedFlag("RWU", 19)
                .WithTaggedFlag("WUF", 20)
                .WithFlag(21, FieldMode.Read, name: "TEACK", valueProviderCallback: _ => transmitEnabled.Value)
                .WithFlag(22, FieldMode.Read, name: "REACK", valueProviderCallback: _ => receiveEnabled.Value)
                .WithReservedBits(23, 2)
                .WithReservedBits(26, 6);

            var icr = Registers.InterruptFlagClear.Define(RegistersCollection)
                .WithTaggedFlag("PECF", 0)
                .WithTaggedFlag("FECF", 1)
                .WithTaggedFlag("NCF", 2)
                .WithTaggedFlag("ORECF", 3)
                .WithTaggedFlag("IDLECF", 4)
                .WithReservedBits(5, 1)
                // this flag is clear on read or write 1
                .WithFlag(6, name: "TCCF",
                    readCallback: (_, __) => { transferComplete.Value = false; },
                    writeCallback: (_, val) => { if(val) transferComplete.Value = false; })
                .WithTaggedFlag("CTSCF", 9)
                .WithReservedBits(10, 1)
                .WithReservedBits(13, 4)
                .WithTaggedFlag("CMCF", 17)
                .WithReservedBits(18, 2)
                .WithTaggedFlag("WUCF", 20)
                .WithReservedBits(21, 11)
                .WithWriteCallback((_, __) => UpdateInterrupt());

            Registers.ReceiveData.Define(RegistersCollection)
                .WithValueField(0, 8, FieldMode.Read, valueProviderCallback: _ => HandleReceiveData(), name: "RDR")
                .WithReservedBits(8, 24);

            Registers.TransmitData.Define(RegistersCollection)
                .WithValueField(0, 8, 
                    // reading this register will intentionally return the last written value
                    writeCallback: (_, val) => HandleTransmitData(val), name: "TDR")
                .WithReservedBits(8, 24);

            if(lowPowerMode)
            {
                cr1
                    .WithReservedBits(15, 1)
                    .WithReservedBits(26, 2);

                cr2
                    .WithReservedBits(5, 2)
                    .WithReservedBits(8, 4)
                    .WithReservedBits(14, 1)
                    .WithReservedBits(20, 4);

                cr3
                    .WithReservedBits(1, 2)
                    .WithReservedBits(4, 2)
                    .WithReservedBits(11, 1)
                    .WithReservedBits(17, 3)
                    .WithReservedBits(24, 1);

                request
                    .WithReservedBits(0, 1)
                    .WithReservedBits(5, 1);

                isr
                    .WithReservedBits(8, 1)
                    .WithReservedBits(11, 2)
                    .WithReservedBits(14, 2)
                    .WithReservedBits(25, 1);

                icr
                    .WithReservedBits(7, 2)
                    .WithReservedBits(11, 2);
            }
            else
            {
                cr1
                    .WithFlag(15, out over8, name: "OVER8")
                    .WithTaggedFlag("RTOIE", 26)
                    .WithTaggedFlag("EOBIE", 27);

                cr2
                    .WithTaggedFlag("LBDL", 5)
                    .WithTaggedFlag("LBDIE", 6)
                    .WithTaggedFlag("LBCL", 8)
                    .WithTaggedFlag("CPHA", 9)
                    .WithTaggedFlag("CPOL", 10)
                    .WithTaggedFlag("CLKEN", 11)
                    .WithTaggedFlag("LINEN", 14)
                    .WithTaggedFlag("ABREN", 20)
                    .WithTag("ABRMOD", 21, 2)
                    .WithTaggedFlag("RTOEN", 23);

                cr3
                    .WithTaggedFlag("IREN", 1)
                    .WithTaggedFlag("IRLP", 2)
                    .WithTaggedFlag("NACK", 4)
                    .WithTaggedFlag("SCEN", 5)
                    .WithTaggedFlag("ONEBIT", 11)
                    .WithTag("SCARCNT", 17, 3)
                    .WithTaggedFlag("TCBGTIE", 24);

                request
                    .WithTaggedFlag("ABRRQ", 0)
                    .WithTaggedFlag("TXFRQ", 5);

                isr
                    .WithTaggedFlag("LBDF", 8)
                    .WithTaggedFlag("RTOF", 11)
                    .WithTaggedFlag("EOBF", 12)
                    .WithTaggedFlag("ABRE", 14)
                    .WithTaggedFlag("ABRF", 15)
                    .WithTaggedFlag("TCBGT", 25);

                icr
                    .WithTaggedFlag("TCBGTCF", 7)
                    .WithTaggedFlag("LBDCF", 8)
                    .WithTaggedFlag("RTOCF", 11)
                    .WithTaggedFlag("EOBCF", 12);
            }
        }

        private void HandleTransmitData(uint value)
        {
            if(transmitEnabled.Value && enabled.Value)
            {
                base.TransmitCharacter((byte)value);
                transferComplete.Value = true;
                UpdateInterrupt();
            }
            else
            {
                this.Log(LogLevel.Warning, "Char was to be sent, but the transmitter (or the whole USART) is not enabled. Ignoring.");
            }
        }

        private uint HandleReceiveData()
        {
            if(!TryGetCharacter(out var result))
            {
                this.Log(LogLevel.Warning, "No characters in queue.");
            }
            return result;
        }

        private void UpdateInterrupt()
        {
            var transmitRegisterEmptyInterrupt = transmitRegisterEmptyInterruptEnabled.Value; // we assume that transmit register is always empty
            var transferCompleteInterrupt = transferComplete.Value && transferCompleteInterruptEnabled.Value;
            var readRegisterNotEmptyInterrupt = Count != 0 && readRegisterNotEmptyInterruptEnabled.Value;
            
            IRQ.Set(transmitRegisterEmptyInterrupt || transferCompleteInterrupt || readRegisterNotEmptyInterrupt);
        }

        private IFlagRegisterField parityControlEnabled;
        private IFlagRegisterField paritySelection;
        private IFlagRegisterField transmitRegisterEmptyInterruptEnabled;
        private IFlagRegisterField transferCompleteInterruptEnabled;
        private IFlagRegisterField transferComplete;
        private IFlagRegisterField readRegisterNotEmptyInterruptEnabled;
        private IFlagRegisterField transmitEnabled;
        private IFlagRegisterField receiveEnabled;
        private IFlagRegisterField enabled;
        private IValueRegisterField baudRateDivisor;
        private IValueRegisterField stopBits;
        private IFlagRegisterField over8;

        private readonly uint frequency;
        private readonly bool lowPowerMode;

        private uint BaudRateMultiplier => lowPowerMode ? 256u : over8.Value ? 2u : 1u;

        private enum Registers
        {
            ControlRegister1   = 0x0,
            ControlRegister2   = 0x4,
            ControlRegister3   = 0x8,
            BaudRate           = 0xC,
            ReceiverTimeout    = 0x14,
            Request            = 0x18,
            InterruptAndStatus = 0x1C,
            InterruptFlagClear = 0x20,
            ReceiveData        = 0x24,
            TransmitData       = 0x28,
        }
    }
}

