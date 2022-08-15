﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using vxlapi_NET;

namespace VectorController.Processor
{
    internal class CanFdBus : CommonVector
    {

        // -----------------------------------------------------------------------------------------------
        // DLL Import for RX events
        // -----------------------------------------------------------------------------------------------
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern int WaitForSingleObject(int handle, int timeOut);
        // -----------------------------------------------------------------------------------------------

        public XLDriver driver { get; set; }
        public XLDefine.XL_HardwareType hardwareType { get; set; }

        private static uint canFdModeNoIso = 0;      // Global CAN FD ISO (default) / no ISO mode flag

        private static int eventHandle = -1;

        public CanFdBus(XLDriver xLDriver, XLDefine.XL_HardwareType xL_HardwareType) : base(xLDriver, xL_HardwareType, XLDefine.XL_BusTypes.XL_BUS_TYPE_CAN)
        {
            driver = xLDriver;
            hardwareType = xL_HardwareType;
        }

        [STAThread]
        public void TestCanFDBus() 
        {
            Trace.WriteLine("-------------------------------------------------------------------");
            Trace.WriteLine("                     VectorController                       ");
            Trace.WriteLine("");
            Trace.WriteLine("-------------------------------------------------------------------");
            Trace.WriteLine("vxlapi_NET        : " + typeof(XLDriver).Assembly.GetName().Version);

            OpenDriver();
            GetDriverConfig();

            GetDLLVesrion();
            Trace.WriteLine(GetChannelCount());

            foreach (var item in GetListOfChannels())
            {
                Trace.WriteLine("*********************");
                Trace.WriteLine($"Channel name: {item.ChannelName}");
                Trace.WriteLine($"Channel mask: {item.ChannelMask}");
                Trace.WriteLine($"Transceiver name: {item.TransceiverName}");
                Trace.WriteLine($"Serial number: {item.SerialNumber}");
                Trace.WriteLine($"Channel compatible CanFD: {item.CanFdCompatible}");

                Trace.WriteLine("---------------------");
            }

            GetAppConfigAndSetAppConfig();
            RequestTheUserToAssignChannels();
            PrintConfig();
            GetAccesMask();
            PrintAccessMask();
            OpenPort();
            SetCanFdConfiguration();
            SetNotification();
            ActivateChannel();
            //ResetClock();
            GetXlDriverConfiguration();
            RunRxThread();


            //for (int i = 0; i < 20; i++)
            //{
            //    CanFdTransmit();

            //}
        }


        //*******************************
        //**** Special CAN FD Bus API below
        //*******************************

        // xlCanFdSetConfiguration - DONE
        // xlCanTransmitEx
        // xlCanReceive
        // xlCanGetEventString

        internal XLDefine.XL_Status SetCanFdConfiguration() 
        {
            XLClass.XLcanFdConf canFdConf = new XLClass.XLcanFdConf();

            // arbitration bitrate
            canFdConf.arbitrationBitRate = 500000;  //Arbitration CAN bus timing for nominal / arbitration bit rate in bit/s.
            canFdConf.tseg1Abr = 6;  //Arbitration CAN bus timing tseg1.Range: 1 < tseg1Abr < 255.
            canFdConf.tseg2Abr = 3;  //Arbitration CAN bus timing tseg2.Range: 1 < tseg2Abr < 255.
            canFdConf.sjwAbr = 2; // Arbitration CAN bus timing value (sample jump width).Range: 0 < sjwAbr <= min(tseg2Abr, 128).

            // data bitrate
            canFdConf.dataBitRate = canFdConf.arbitrationBitRate * 4;  // CAN bus timing for data bit rate in bit/s.Range: dataBitRate >= max(arbitrationBitRate, 25000).
            canFdConf.tseg1Dbr = 6;  //Data phase CAN bus timing for data tseg1.Range: 1 < tseg1Dbr < 127.
            canFdConf.tseg2Dbr = 3;  //Data phase CAN bus timing for data tseg2.Range: 1 < tseg2Dbr < 127.
            canFdConf.sjwDbr = 2;  // Arbitration CAN bus timing value (sample jump width).Range: 0 < sjwAbr <= min(tseg2Abr, 128).

            if (canFdModeNoIso > 0)
            {
                canFdConf.options = (byte)XLDefine.XL_CANFD_ConfigOptions.XL_CANFD_CONFOPT_NO_ISO;
            }
            else
            {
                canFdConf.options = 0;
            }

            XLDefine.XL_Status status = driver.XL_CanFdSetConfiguration(portHandle, accessMask, canFdConf);
            Trace.WriteLine("\n\nSet CAN FD Config     : " + status);
            if (status != XLDefine.XL_Status.XL_SUCCESS) PrintFunctionError("SetCanFdConfiguration");

            return status;
        }


        internal XLDefine.XL_Status GetXlDriverConfiguration() 
        {
            // Get XL Driver configuration to get the actual setup parameter
            XLDefine.XL_Status status = driver.XL_GetDriverConfig(ref driverConfig);
            if (status != XLDefine.XL_Status.XL_SUCCESS) PrintFunctionError("GetXlDriverConfiguration");

            if (canFdModeNoIso > 0)
            {
                Trace.WriteLine("CAN FD mode           : NO ISO");
            }
            else
            {
                Trace.WriteLine("CAN FD mode           : ISO");
            }
            Trace.WriteLine("TX Arb. BitRate       : " + driverConfig.channel[txCi].busParams.dataCanFd.arbitrationBitRate
                            + "Bd, Data Bitrate: " + driverConfig.channel[txCi].busParams.dataCanFd.dataBitRate + "Bd");

            return status;
        }


        /// <summary>
        /// Run Rx Thread
        /// </summary>
        private void RunRxThread()
        {
            Trace.WriteLine("Start Rx thread...");
            rxThreadDDD = new Thread(new ThreadStart(RXThread));
            rxThreadDDD.Start();
        }


        public void RXThread()
        {
            // Create new object containing received data 
            XLClass.XLcanRxEvent receivedEvent = new XLClass.XLcanRxEvent();

            // Result of XL Driver function calls
            XLDefine.XL_Status xlStatus = XLDefine.XL_Status.XL_SUCCESS;

            // Result values of WaitForSingleObject 
            XLDefine.WaitResults waitResult = new XLDefine.WaitResults();


            // Note: this thread will be destroyed by MAIN
            while (true)
            {
                // Wait for hardware events
                waitResult = (XLDefine.WaitResults)WaitForSingleObject(eventHandle, 1000);

                // If event occurred...
                if (waitResult != XLDefine.WaitResults.WAIT_TIMEOUT)
                {
                    // ...init xlStatus first
                    xlStatus = XLDefine.XL_Status.XL_SUCCESS;

                    // afterwards: while hw queue is not empty...
                    while (xlStatus != XLDefine.XL_Status.XL_ERR_QUEUE_IS_EMPTY)
                    {
                        // ...block RX thread to generate RX-Queue overflows
                        while (blockRxThread) Thread.Sleep(1000);

                        // ...receive data from hardware.
                        xlStatus = base.driver.XL_CanReceive(portHandle, ref receivedEvent);

                        //  If receiving succeed....
                        if (xlStatus == XLDefine.XL_Status.XL_SUCCESS)
                        {
                            Trace.WriteLine(driver.XL_CanGetEventString(receivedEvent));

                        }
                    }
                }
                // No event occurred
            }
        }

        public void CanFdTransmit()
        {
            XLDefine.XL_Status txStatus;

            XLClass.xl_canfd_event_collection xlEventCollection = new XLClass.xl_canfd_event_collection(1);

            xlEventCollection.xlCANFDEvent[0].tag = XLDefine.XL_CANFD_TX_EventTags.XL_CAN_EV_TAG_TX_MSG;
            xlEventCollection.xlCANFDEvent[0].tagData.canId = 0x100;
            xlEventCollection.xlCANFDEvent[0].tagData.dlc = XLDefine.XL_CANFD_DLC.DLC_CAN_CANFD_8_BYTES;
            xlEventCollection.xlCANFDEvent[0].tagData.msgFlags = XLDefine.XL_CANFD_TX_MessageFlags.XL_CAN_TXMSG_FLAG_BRS | XLDefine.XL_CANFD_TX_MessageFlags.XL_CAN_TXMSG_FLAG_EDL;
            xlEventCollection.xlCANFDEvent[0].tagData.data[0] = 1;
            xlEventCollection.xlCANFDEvent[0].tagData.data[1] = 1;
            xlEventCollection.xlCANFDEvent[0].tagData.data[2] = 2;
            xlEventCollection.xlCANFDEvent[0].tagData.data[3] = 2;
            xlEventCollection.xlCANFDEvent[0].tagData.data[4] = 3;
            xlEventCollection.xlCANFDEvent[0].tagData.data[5] = 3;
            xlEventCollection.xlCANFDEvent[0].tagData.data[6] = 4;
            xlEventCollection.xlCANFDEvent[0].tagData.data[7] = 4;

            uint messageCounterSent = 0;
            txStatus = base.driver.XL_CanTransmitEx(portHandle, txMask, ref messageCounterSent, xlEventCollection);
            Trace.WriteLine($"Transmit Message      : {txStatus} { messageCounterSent}");
        }



        /// <summary>
        /// Request the user to assign channels until both CAN1 (Tx) and CAN2 (Rx) are assigned to usable channels
        /// </summary>
        private void RequestTheUserToAssignChannels()
        {
            if (!GetAppChannelAndTestIsOk(0, ref txMask, ref txCi) || !GetAppChannelAndTestIsOk(1, ref rxMask, ref rxCi))
            {
                PrintAssignErrorAndPopupHwConf();
            }
        }

        // -----------------------------------------------------------------------------------------------
        /// <summary>
        /// Retrieve the application channel assignment and test if this channel can be opened
        /// </summary>
        // -----------------------------------------------------------------------------------------------
        private bool GetAppChannelAndTestIsOk(uint appChIdx, ref UInt64 chMask, ref int chIdx)
        {
            XLDefine.XL_Status status = driver.XL_GetApplConfig(appName, appChIdx, ref hwType, ref hwIndex, ref hwChannel, XLDefine.XL_BusTypes.XL_BUS_TYPE_CAN);
            if (status != XLDefine.XL_Status.XL_SUCCESS)
            {
                Trace.WriteLine("XL_GetApplConfig      : " + status);
                PrintFunctionError("GetAppChannelAndTestIsOk");
            }

            chMask = driver.XL_GetChannelMask(hwType, (int)hwIndex, (int)hwChannel);
            chIdx = driver.XL_GetChannelIndex(hwType, (int)hwIndex, (int)hwChannel);
            if (chIdx < 0 || chIdx >= driverConfig.channelCount)
            {
                // the (hwType, hwIndex, hwChannel) triplet stored in the application configuration does not refer to any available channel.
                return false;
            }

            if ((driverConfig.channel[chIdx].channelBusCapabilities & XLDefine.XL_BusCapabilities.XL_BUS_ACTIVE_CAP_CAN) == 0)
            {
                // CAN is not available on this channel
                return false;
            }

            if (canFdModeNoIso > 0)
            {
                if ((driverConfig.channel[chIdx].channelCapabilities & XLDefine.XL_ChannelCapabilities.XL_CHANNEL_FLAG_CANFD_BOSCH_SUPPORT) == 0)
                {
                    Trace.WriteLine($"{driverConfig.channel[chIdx].name.TrimEnd(' ', '\0')} {driverConfig.channel[chIdx].transceiverName.TrimEnd(' ', '\0')} does not support CAN FD NO-ISO");
                    return false;
                }
            }
            else
            {
                if ((driverConfig.channel[chIdx].channelCapabilities & XLDefine.XL_ChannelCapabilities.XL_CHANNEL_FLAG_CANFD_ISO_SUPPORT) == 0)
                {
                    Trace.WriteLine($"{driverConfig.channel[chIdx].name.TrimEnd(' ', '\0')} {driverConfig.channel[chIdx].transceiverName.TrimEnd(' ', '\0')} does not support CAN FD ISO");
                    return false;
                }
            }

            return true;
        }



    }
}
