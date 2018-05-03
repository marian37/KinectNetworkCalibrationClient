using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Kinect;
using System.Windows;
using System.Windows.Media;

namespace ClientApp
{
    public class KinectAdapter : IDisposable
    {
        private const int COLOR_SPACE_MAX_WIDTH = 1920;
        private const int COLOR_SPACE_MAX_HEIGHT = 1080;

        private KinectSensor kinectSensor;

        private MultiSourceFrameReader multiSourceFrameReader;

        private Body[] bodies = null;

        private MainWindow mainWindow;

        public bool sensorOpened;

        public float InferredZPosition = 0.1f;

        private CoordinateMapper coordinateMapper;

        private Color[] color = {
            Colors.Red, Colors.DarkRed,
            Colors.Green, Colors.DarkGreen,
            Colors.Blue, Colors.DarkBlue,
            Colors.Cyan, Colors.DarkCyan,
            Colors.Magenta, Colors.DarkMagenta,
            Colors.Yellow, Colors.Orange
        };

        public KinectAdapter(MainWindow mainWindow)
        {
            this.kinectSensor = KinectSensor.GetDefault();
            this.kinectSensor.IsAvailableChanged += this.Sensor_IsAvailableChanged;

            this.coordinateMapper = this.kinectSensor.CoordinateMapper;

            this.mainWindow = mainWindow;
        }

        public void Dispose()
        {
            this.mainWindow.cameraImage.Source = null;
            this.mainWindow.bodyCanvas.Children.Clear();
            this.changeNumberOfTrackedPeople(0);

            if (this.multiSourceFrameReader != null)
            {
                this.multiSourceFrameReader.MultiSourceFrameArrived -= this.Reader_MultiSourceFrameArrived;
                this.multiSourceFrameReader.Dispose();
                this.multiSourceFrameReader = null;
            }

            if (this.kinectSensor != null)
            {
                this.kinectSensor.IsAvailableChanged -= this.Sensor_IsAvailableChanged;
                this.kinectSensor.Close();
                this.kinectSensor = null;
            }
        }

        public void Close()
        {
            if (this.multiSourceFrameReader != null)
            {
                this.multiSourceFrameReader.MultiSourceFrameArrived -= this.Reader_MultiSourceFrameArrived;
                this.multiSourceFrameReader.Dispose();
                this.multiSourceFrameReader = null;
            }

            if (this.kinectSensor != null)
            {
                this.kinectSensor.Close();
            }

            this.sensorOpened = false;
            this.changeStatus(this.kinectSensor.IsAvailable, this.sensorOpened);
            this.changeNumberOfTrackedPeople(0);
            this.mainWindow.cameraImage.Source = null;
            this.mainWindow.bodyCanvas.Children.Clear();
            this.mainWindow.client.Bodies = null;
        }

        public void Open()
        {
            if (this.kinectSensor == null)
            {
                this.kinectSensor = KinectSensor.GetDefault();
            }
            this.kinectSensor.Open();

            this.multiSourceFrameReader = this.kinectSensor.OpenMultiSourceFrameReader(FrameSourceTypes.Color | FrameSourceTypes.Body);
            this.multiSourceFrameReader.MultiSourceFrameArrived += this.Reader_MultiSourceFrameArrived;

            this.sensorOpened = true;
            this.changeStatus(this.kinectSensor.IsAvailable, this.sensorOpened);
        }

        private void Sensor_IsAvailableChanged(object sender, IsAvailableChangedEventArgs e)
        {
            this.changeStatus(e.IsAvailable, this.sensorOpened);
        }

        private void changeStatus(bool isAvailable, bool sensorOpened)
        {
            this.mainWindow.kinectStatus.Text = isAvailable ? (sensorOpened ? "Running" : "Paused") : "No sensor";
        }

        private void changeNumberOfTrackedPeople(int trackedPeople)
        {
            this.mainWindow.trackedPeopleLbl.Content = trackedPeople;
        }

        private void Reader_MultiSourceFrameArrived(object sender, MultiSourceFrameArrivedEventArgs e)
        {
            MultiSourceFrame frameReference = e.FrameReference.AcquireFrame();

            using (ColorFrame colorFrame = frameReference.ColorFrameReference.AcquireFrame())
            {
                if (colorFrame != null)
                {
                    mainWindow.cameraImage.Source = colorFrame.ToBitmap();
                }
            }

            bool dataReceived = false;
            using (BodyFrame bodyFrame = frameReference.BodyFrameReference.AcquireFrame())
            {
                if (bodyFrame != null)
                {
                    mainWindow.bodyCanvas.Children.Clear();

                    if (this.bodies == null)
                    {
                        this.bodies = new Body[bodyFrame.BodyCount];
                    }

                    bodyFrame.GetAndRefreshBodyData(bodies);
                    dataReceived = true;
                }
            }

            if (dataReceived)
            {
                int bodyColor = 0;

                if (this.bodies != null)
                {
                    mainWindow.client.Bodies = this.bodies;

                    int maxBodies = this.kinectSensor.BodyFrameSource.BodyCount;
                    int trackedBodies = 0;
                    for (int i = 0; i < maxBodies; i++)
                    {
                        if (bodies[i] != null)
                        {
                            if (bodies[i].IsTracked)
                            {
                                trackedBodies++;

                                IReadOnlyDictionary<JointType, Joint> joints = bodies[i].Joints;
                                Dictionary<JointType, Point> jointPoints = new Dictionary<JointType, Point>();

                                foreach (JointType jointType in joints.Keys)
                                {
                                    CameraSpacePoint position = joints[jointType].Position;
                                    if (position.Z < 0)
                                    {
                                        position.Z = InferredZPosition;
                                    }

                                    ColorSpacePoint colorSpacePoint = this.coordinateMapper.MapCameraPointToColorSpace(position);
                                    double x = float.IsInfinity(colorSpacePoint.X) ? 0 : colorSpacePoint.X / COLOR_SPACE_MAX_WIDTH * mainWindow.bodyCanvas.ActualWidth;
                                    double y = float.IsInfinity(colorSpacePoint.Y) ? 0 : colorSpacePoint.Y / COLOR_SPACE_MAX_HEIGHT * mainWindow.bodyCanvas.ActualHeight;
                                    jointPoints[jointType] = new Point(x, y);
                                }

                                mainWindow.bodyCanvas.DrawSkeleton(joints, jointPoints, color[2 * bodyColor], color[2 * bodyColor + 1]);
                            }
                        }
                        bodyColor++;
                    }

                    changeNumberOfTrackedPeople(trackedBodies);
                }
            }
        }
    }
}
