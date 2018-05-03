using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Net.NetworkInformation;
using Newtonsoft.Json.Bson;
using Newtonsoft.Json;
using SharedData;
using System.Threading.Tasks;
using Microsoft.Kinect;
using System.Threading;
using System.Diagnostics;
using System.Windows.Media.Animation;
using System.Text;

namespace ClientApp
{

    public class ChangeStatusEventArgs : EventArgs
    {
        public string status { get; set; }
    }

    public class Client
    {
        public const int DEFAULT_PORT = 8000;

        public const int TIMEOUT = 10;

        private const int BUFFER_SIZE = 16384;

        private const int WAIT_MILISECONDS = 1000 / 24;

        private const float MOVING_DISTANCE_EPSILON = 0.01f;

        private Socket sender;

        private MainWindow mainWindow;

        private volatile bool mainLoopRunning;

        private TimeSpan timestampDiff;

        public event EventHandler<ChangeStatusEventArgs> ChangeStatusEventHandler;

        private Object myLock = new Object();

        private SerializableBody[] lastBodies = null;

        private JointType[] importantJoints = new JointType[] { JointType.Neck, JointType.SpineMid, JointType.ShoulderLeft, JointType.ShoulderRight, JointType.Head, JointType.HandLeft, JointType.HandRight, JointType.SpineBase };

        private Body[] bodies;

        public Body[] Bodies
        {
            get
            {
                if (this.bodies != null)
                {
                    Body[] bodiesTemp = new Body[this.bodies.Length];
                    Array.Copy(this.bodies, bodiesTemp, this.bodies.Length);
                    return bodiesTemp;
                }
                else
                    return new Body[0];
            }
            set
            {
                lock (myLock)
                {
                    this.bodies = value;
                }
            }
        }

        public Client(MainWindow mainWindow)
        {
            this.mainWindow = mainWindow;
        }

        protected virtual void ChangeStatus(string status)
        {
            Console.WriteLine(status);
            ChangeStatusEventArgs args = new ChangeStatusEventArgs();
            args.status = status;
            ChangeStatusEventHandler?.Invoke(this, args);
        }

        public bool SearchAndConnect(IPAddress ipAddress, IPAddress submask, int port)
        {
            ChangeStatus("Searching...");
            List<IPAddress> ipAddressRange = getAddressRange(ipAddress, submask);
            Console.WriteLine(ipAddressRange.Count);
            for (int i = 0; i < ipAddressRange.Count; i++)
            {
                Console.WriteLine(i + " " + ipAddressRange[i] + " " + port);
                try
                {
                    IPEndPoint ipEndPoint = new IPEndPoint(ipAddressRange[i], port);
                    sender = new Socket(ipAddressRange[i].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    sender.NoDelay = false;
                    IAsyncResult result = sender.BeginConnect(ipEndPoint, null, null);
                    bool success = result.AsyncWaitHandle.WaitOne(TIMEOUT, true);
                    if (sender.Connected)
                    {
                        ChangeStatus("Client connected to: " + sender.RemoteEndPoint.ToString() + ".");
                        mainLoopRunning = true;
                        Thread newThread = new Thread(MainLoop);
                        newThread.Start();
                        return true;
                    }
                    else
                    {
                        Console.WriteLine("Failed to connect to: " + ipAddressRange[i].ToString());
                        sender.Close();
                    }
                }
                catch (Exception exception)
                {
                    Console.WriteLine(exception.ToString());
                }
            }

            ChangeStatus("Search unsuccessful.");
            return false;
        }

        public void MainLoop()
        {
            timestampDiff = TimeSync();

            while (mainLoopRunning)
            {
                MessageData data = PrepareData(this.Bodies);
                Send(data);
                Thread.Sleep(WAIT_MILISECONDS);
            }
        }

        private TimeSpan TimeSync()
        {
            Stopwatch stopwatch = new Stopwatch();
            TimeSpan minRTT = TimeSpan.MaxValue;
            TimeSpan diff = TimeSpan.Zero;

            for (int i = 0; i < 3; i++)
            {
                MessageData data = PrepareTimeSyncMessage();
                stopwatch.Restart();
                Send(data);
                DateTime timeStamp = Receive();
                stopwatch.Stop();
                Console.WriteLine("Sent time sync. " + timeStamp);

                TimeSpan rtt = stopwatch.Elapsed;
                Console.WriteLine("RTT: " + rtt);
                if (rtt < minRTT)
                {
                    diff = timeStamp.Add(new TimeSpan(rtt.Ticks / 2)).Subtract(DateTime.Now);
                }
            }

            Console.WriteLine("Diff: " + diff);

            return diff;
        }

        private MessageData PrepareData(Body[] bodies)
        {
            MessageData message = new MessageData();
            message.MessageType = SharedData.Type.Data;
            message.SrcIPAddress = sender.LocalEndPoint.ToString();
            message.Timestamp = DateTime.Now.Add(timestampDiff);
            SerializableBody[] serializableBodies = new SerializableBody[bodies.Length];
            for (int i = 0; i < bodies.Length; i++)
            {
                serializableBodies[i] = PrepareBody(bodies[i], (lastBodies == null || lastBodies.Length <= i) ? null : lastBodies[i]);
            }
            message.Bodies = serializableBodies;
            lastBodies = serializableBodies;
            return message;
        }

        private SerializableBody PrepareBody(Body body, SerializableBody lastBody)
        {
            MovingState movingState = IsMoving(body, lastBody);

            CalibrationState calibrationState = CalibrationState.Uncalibrated;
            RaisedHand raisedHand = RaisedHand.None;
            bool gestureDetected = false;

            if (lastBody != null)
            {
                switch (lastBody.CalibrationState)
                {
                    // raise hand
                    case CalibrationState.Uncalibrated:
                        gestureDetected = false;
                        raisedHand = IsRaisedHand(body);
                        calibrationState = (raisedHand == RaisedHand.None) ? CalibrationState.Uncalibrated : CalibrationState.RaisedHand;
                        break;

                    // wait to stand
                    case CalibrationState.RaisedHand:
                        if (lastBody.GestureDetected)
                        {
                            gestureDetected = false;
                            calibrationState = CalibrationState.SavedPose;
                            raisedHand = lastBody.RaisedHand;
                            break;
                        }

                        if (movingState == MovingState.Stand)
                        {
                            gestureDetected = true;
                            calibrationState = CalibrationState.RaisedHand;
                            raisedHand = lastBody.RaisedHand;
                            // show notification
                            this.mainWindow.Dispatcher.Invoke(
                                new Action(() =>
                                {
                                    Storyboard sb = this.mainWindow.notificationEllipse.FindResource("notificationStoryBoard") as Storyboard;
                                    sb.Begin();
                                }));
                        }
                        else
                        {
                            gestureDetected = false;
                            raisedHand = IsRaisedHand(body);
                            calibrationState = (raisedHand == RaisedHand.None) ? CalibrationState.Uncalibrated : CalibrationState.RaisedHand;
                        }
                        break;

                    // lower hand
                    case CalibrationState.SavedPose:
                        gestureDetected = false;
                        raisedHand = IsRaisedHand(body);
                        calibrationState = (raisedHand == RaisedHand.None) ? CalibrationState.Uncalibrated : CalibrationState.SavedPose;
                        break;
                }
            }

            return new SerializableBody(body, movingState, calibrationState, raisedHand, gestureDetected);
        }

        public MovingState IsMoving(Body body, SerializableBody lastBody)
        {
            if (lastBody == null || !body.IsTracked)
                return MovingState.Moving;

            foreach (JointType jointType in importantJoints)
            {
                if (body.Joints[jointType].TrackingState != TrackingState.Tracked)
                    return MovingState.Moving;
            }

            float diffSquareDistance = 0;
            foreach (KeyValuePair<JointType, Joint> entry in body.Joints)
            {
                diffSquareDistance += EuclideanSquareDistance(entry.Value, lastBody.Joints[entry.Key]);
            }
            Console.WriteLine("Moving Diff: " + diffSquareDistance);
            if (diffSquareDistance <= MOVING_DISTANCE_EPSILON)
            {
                return MovingState.Stand;
            }
            else
            {
                return MovingState.Moving;
            }
        }

        public RaisedHand IsRaisedHand(Body body)
        {
            if (body == null || !body.IsTracked)
                return RaisedHand.None;

            if (body.Joints[JointType.HandRight].Position.Y > body.Joints[JointType.Head].Position.Y)
                return RaisedHand.Right;

            if (body.Joints[JointType.HandLeft].Position.Y > body.Joints[JointType.Head].Position.Y)
                return RaisedHand.Left;

            return RaisedHand.None;
        }

        public float EuclideanSquareDistance(Joint joint1, Joint joint2)
        {
            float distance = 0;

            distance += (joint1.Position.X - joint2.Position.X) * (joint1.Position.X - joint2.Position.X);
            distance += (joint1.Position.Y - joint2.Position.Y) * (joint1.Position.Y - joint2.Position.Y);
            distance += (joint1.Position.Z - joint2.Position.Z) * (joint1.Position.Z - joint2.Position.Z);

            return distance;
        }

        private MessageData PrepareTimeSyncMessage()
        {
            MessageData message = new MessageData();
            message.MessageType = SharedData.Type.TimeSync;
            message.SrcIPAddress = sender.LocalEndPoint.ToString();
            message.Timestamp = DateTime.Now;
            return message;
        }

        public void Send(MessageData message)
        {
            MemoryStream ms = new MemoryStream();
            using (BsonDataWriter writer = new BsonDataWriter(ms))
            {
                JsonSerializer jsonSerializer = new JsonSerializer();
                jsonSerializer.Serialize(writer, message);
            }

            byte[] msg = ms.ToArray();
            int bytesSend = sender.Send(msg);
            Console.WriteLine("Sent: " + bytesSend);
            if (message.Bodies != null)
                foreach (SerializableBody body in message.Bodies)
                {
                    if (body.IsTracked)
                    {
                        Console.WriteLine(body.TrackingID + " " + body.GestureDetected + " " + body.CalibrationState + " " + body.MovingState + " " + body.RaisedHand + " " + message.Timestamp);
                    }
                }
        }

        private DateTime Receive()
        {
            byte[] receiveBuffer = new byte[BUFFER_SIZE];
            sender.Receive(receiveBuffer);

            if (sender.Available == 0)
            {
                MemoryStream ms = new MemoryStream(receiveBuffer.ToArray());
                MessageData message;
                JsonSerializer jsonSerializer = new JsonSerializer();
                using (BsonDataReader reader = new BsonDataReader(ms))
                {
                    message = jsonSerializer.Deserialize<MessageData>(reader);
                    Console.WriteLine("Reading TS: " + message.Timestamp);
                    return message.Timestamp;
                }
            }
            else
            {
                throw new Exception("Small receive buffer.");
            }
        }

        public void Disconnect()
        {
            mainLoopRunning = false;
            sender.Shutdown(SocketShutdown.Both);
            sender.Close();
            ChangeStatus("Disconnected.");
        }

        public List<IPAddress> getPossibleIpAddresses()
        {
            IPHostEntry ipHost = Dns.GetHostEntry(Dns.GetHostName());
            return ipHost.AddressList.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).ToList();
        }

        public static IPAddress GetSubnetMask(IPAddress address)
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (UnicastIPAddressInformation unicastIPAddressInformation in adapter.GetIPProperties().UnicastAddresses)
                {
                    if (unicastIPAddressInformation.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        if (address.Equals(unicastIPAddressInformation.Address))
                        {
                            return unicastIPAddressInformation.IPv4Mask;
                        }
                    }
                }
            }
            throw new ArgumentException(string.Format("Can't find subnetmask for IP address '{0}'", address));
        }

        public static List<IPAddress> getAddressRange(IPAddress clientAddress, IPAddress subnetMask)
        {
            byte[] clientAddressBytes = clientAddress.GetAddressBytes();
            byte[] subnetMaskBytes = subnetMask.GetAddressBytes();

            int length = clientAddressBytes.Length;

            byte[] startIPBytes = new byte[length];
            byte[] endIPBytes = new byte[length];

            for (int i = 0; i < length; i++)
            {
                startIPBytes[length - 1 - i] = (byte)(clientAddressBytes[i] & subnetMaskBytes[i]);
                endIPBytes[length - 1 - i] = (byte)(clientAddressBytes[i] | ~subnetMaskBytes[i]);
            }

            uint start = BitConverter.ToUInt32(startIPBytes, 0);
            uint end = BitConverter.ToUInt32(endIPBytes, 0);

            List<IPAddress> result = new List<IPAddress>();

            for (uint i = start + 1; i < end; i++)
            {
                byte[] newIP = BitConverter.GetBytes(i);
                result.Add(new IPAddress(newIP.Reverse().ToArray()));
            }

            return result;
        }
    }
}
