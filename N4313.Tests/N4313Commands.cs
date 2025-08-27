using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace N4313.Tests
{
    public static class N4313Commands
    {
        private const char ACK = '\x06';
        private const char SYN = '\x16';
        public const string ActivationSequence = "\x16" + "T" + "\x0D";
        public const string ExpectedScanResult = "123456789";
        public const string ContinuousTestTrigger = "Continuous_mode_test";
        public static Dictionary<string, string> DeviceResponses = new Dictionary<string, string>()
            {
                { "REVINF", "REVINFProduct Name: Laser Engine-N4300\r\n" +
                    "Boot Revision: CA000064BCC\r\n" +
                    "Software Part Number: CA000064BCC\r\n" +
                    "Software Revision: 15448|/tags/CA000064BCC\r\n" +
                    "Serial Number: 20067B450A\r\n" +
                    "Supported IF: Standard\r\n" +
                    $"PCB Assembly ID: 0{ACK}." },
                { "Test", "Test\r\n" },
                { "PAPPM3!", $"PAPPM3{ACK}!" },
                { "DEFALT!", $"DEFALT{ACK}!" },
                { ActivationSequence, ExpectedScanResult + "\r" },
                { ContinuousTestTrigger, ExpectedScanResult + "\r"}
            };
    }
}