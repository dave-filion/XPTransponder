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

namespace XPTransponder
{
    class Program
    {

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
            { "btn_stby", "sim/transponder/transponder_stby" },
            { "btn_alt", "sim/transponder/transponder_alt" },
            { "btn_id", "sim/transponder/transponder_id" },
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
            while (true)
            {
                ConsumeMessagesOffEventQueue();
            }

            Console.WriteLine("DONE");
            _arduinoReadContinue = false;
            _arduinoSerialPort.Close();
            sender.Close();
            listener.Close();
        }

        static async void ConsumeMessagesOffEventQueue()
        {
            try
            {
                while (true)
                {
                    string ev = await eventQueueReader.ReadAsync();
                    Console.WriteLine("rec event: {0} from channel", ev);
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
            }
            catch (ChannelClosedException e)
            {
                Console.WriteLine("event channel closed");
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
            while (true)
            {
                byte[] recBytes = listener.Receive(ref groupEP);
                //Console.WriteLine("rec: " + recBytes.Length);
                //parseDataRef(recBytes);
                // TODO update state based on this
            }
        }


        static void parseDataRef(byte[] data)
        {
            // first 5 chars are DREF+
            // next 4 are value
            byte[] val = new byte[4] { data[5], data[6], data[7], data[8] };
            byte[] next = new byte[500];

            for (int i = 0; i < 500; i++)
            {
                next[i] = data[i + 9];
            }

            string str = Encoding.ASCII.GetString(next);
            float realVal = BitConverter.ToSingle(val, 0);
            Console.WriteLine("{0} : {1}", str, realVal);

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
}
