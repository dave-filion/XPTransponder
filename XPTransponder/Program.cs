using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.IO.Ports;
using System.Threading.Channels;
using System.Timers;
using System.Diagnostics;

namespace XPTransponder
{
    class Program
    {
        static bool reportState = false;

        static string arduinoPort = "COM6";
        static int xpListenPort = 49012;
        static int xpSendPort = 49000;
        private const int listenPort = 49012;
        static UdpClient listener = new UdpClient(listenPort);
        static IPEndPoint groupEP = new IPEndPoint(IPAddress.Any, listenPort);
        static UdpClient sender;
        static IPEndPoint senderEP;
        static SerialPort _arduinoSerialPort;
        static bool _arduinoReadContinue;
        static readonly object xpDataLock = new object();
        static Dictionary<string, float> xpData; // represents current xp data state

        static Channel<string> eventQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        static ChannelWriter<string> eventQueueWriter = eventQueue.Writer;
        static ChannelReader<string> eventQueueReader = eventQueue.Reader;



        static Dictionary<string, string> inputToXPCommands = new Dictionary<string, string>()
        {
            { "btn_0", "sim/transponder/transponder_digit_0" },
            { "btn_1", "sim/transponder/transponder_digit_1" },
            { "btn_2", "sim/transponder/transponder_digit_2" },
            { "btn_3", "sim/transponder/transponder_digit_3" },
            { "btn_4", "sim/transponder/transponder_digit_4" },
            { "btn_5", "sim/transponder/transponder_digit_5" },
            { "btn_6", "sim/transponder/transponder_digit_6" },
            { "btn_7", "sim/transponder/transponder_digit_7" },
            { "btn_8", "sim/transponder/transponder_digit_8" },
            { "btn_9", "sim/transponder/transponder_digit_9" },

            { "btn_on", "sim/transponder/transponder_on" },
            { "btn_off", "sim/transponder/transponder_off" },
            { "btn_stby", "sim/transponder/transponder_standby" },
            { "btn_alt", "sim/transponder/transponder_alt" },
            { "btn_id", "sim/transponder/transponder_ident" },
        };

        static void Main(string[] args)
        {
            // Set up arduino connection
            _arduinoSerialPort = new SerialPort(arduinoPort);
            _arduinoSerialPort.BaudRate = 9600;
            _arduinoReadContinue = true;

            try
            {
                _arduinoSerialPort.Open();
            }
            catch (Exception e)
            {
                Console.WriteLine("Couldnt open arduino stream: {0}", e);
                System.Environment.Exit(1);
            }
            Thread arduinoReadThread = new Thread(arduinoRead);
            arduinoReadThread.Start();

            // Set up XP state listening thread
            Thread readThread = new Thread(listenXPThread);
            readThread.Start();

            // Set up XP message send process
            sender = new UdpClient("192.168.0.4", xpSendPort);

            ConsumeMessagesOffEventQueue(); // blocks

            Console.WriteLine("DONE");
            _arduinoReadContinue = false;
            _arduinoSerialPort.Close();
            sender.Close();
            listener.Close();
        }

        static void ConsumeMessagesOffEventQueue()
        {
            Console.WriteLine("Listening for events from channel");
            try
            {
                while (true)
                {
                    if (eventQueueReader.TryRead(out string ev))
                    {
                        processEvent(ev);
                    }
                }
            }
            catch (ChannelClosedException e)
            {
                Console.WriteLine("event channel closed");
            }
        }

        static void processEvent(string ev)
        {
            if (inputToXPCommands.ContainsKey(ev))
            {
                string xpCmd = inputToXPCommands[ev];
                Console.WriteLine("{0} -> {1} -> sending to XP", ev, xpCmd);
                sendCmdToXP(xpCmd);

            }
            else
            {
                Console.WriteLine("unknown event received: {0}", ev);
            }

        }

        static void sendCmdToXP(string cmd)
        {
            try
            {
                byte[] cmdBytes = getCommandBytes(cmd);
                sender.Send(cmdBytes, cmdBytes.Length);
            }
            catch (Exception e)
            {
                Console.WriteLine("Error sending cmd to xp: {0}", e);
            }
        }

        static void listenXPThread()
        {
            xpData = new Dictionary<string, float>();

            // state reporting for debugging purposes
            if (reportState)
            {
                System.Timers.Timer reportTimer = new System.Timers.Timer(2000); // 5 sec interval

                reportTimer.Elapsed += (sender, e) =>
                {
                    Console.WriteLine("Current state");
                    lock (xpDataLock)
                    {
                        foreach (string key in xpData.Keys)
                        {
                            Console.WriteLine("{0} -> {1}", key, xpData[key]);
                        }

                    }
                };
                reportTimer.Start();

            }

            while (true)
            {

                byte[] recBytes = listener.Receive(ref groupEP);
                //Console.WriteLine("rec: " + recBytes.Length);
                XPDataEntry entry = parseDataRef(recBytes);
                lock (xpDataLock)
                {
                    // if new val is different then old val, signal arduino to update state
                    if (xpData.ContainsKey(entry.name))
                    {
                        float curVal = xpData[entry.name];
                        if (entry.val != curVal)
                        {
                            Console.WriteLine("{0} value changed from {1} -> {2}. Notifying arduino", entry.name, curVal, entry.val);
                            // TODO send update to arduino
                        }
                    }
                    xpData[entry.name] = entry.val;
                }
            }
        }


        static XPDataEntry parseDataRef(byte[] data)
        {
            // first 5 chars are DREF+
            // next 4 are value
            byte[] val = new byte[4] { data[5], data[6], data[7], data[8] };
            byte[] next = new byte[500];
            List<byte> byteList = new List<byte>();

            for (int i = 0; i < 500; i++)
            {
                byte b = data[i + 9];
                if (b.Equals(' ') || b.Equals(0))
                {
                    break;
                }
                else
                {
                    byteList.Add(b);
                }
            }

            string str = Encoding.ASCII.GetString(byteList.ToArray());
            float realVal = BitConverter.ToSingle(val, 0);

            return new XPDataEntry(str, realVal);
        }

        private static byte[] getCommandBytes(string commandString)
        {
            byte[] zero = new byte[1] { 0 };
            string cmd = commandString;
            byte[] cmdB = Encoding.ASCII.GetBytes(cmd);
            return Encoding.ASCII.GetBytes("CMND").Concat(zero).Concat(cmdB).ToArray();
        }

        private static void arduinoRead()
        {
            Console.WriteLine("Arduino read thread started...");
            while (_arduinoReadContinue)
            {
                try
                {
                    string message = _arduinoSerialPort.ReadLine();
                    List<string> events = parseArduinoMessage(message);
                    foreach (string e in events)
                    {
                        eventQueueWriter.TryWrite(e);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Exception Reading from Arduino: {0}", e);
                }
            }
        }

        private static List<string> parseArduinoMessage(string msg)
        {
            List<string> events = new List<string>();

            string[] chunks = msg.Split('|');
            foreach (string chunk in chunks)
            {
                // ! denotes end of message
                if (chunk.StartsWith("!"))
                {
                    //Console.WriteLine("EOM");
                    break;
                }
                else
                {
                    events.Add(chunk);
                }
            }
            return events;
        }
    }

    struct XPDataEntry
    {
        public string name;
        public float val;

        public XPDataEntry(string n, float v)
        {
            name = n;
            val = v;
        }
    }
}
