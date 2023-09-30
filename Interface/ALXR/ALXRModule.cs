﻿using Elements.Core;
using FrooxEngine;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace QuestProModule.ALXR
{
    public class ALXRModule : IInputDriver
    {
        private IPAddress localAddr;
        private const int DEFAULT_PORT = 13191;

        private TcpClient client;
        private NetworkStream stream;
        private Thread tcpThread;
        private CancellationTokenSource cancellationTokenSource;
        private bool connected = false;

        private const int NATURAL_EXPRESSIONS_COUNT = 63;
        private const float SRANIPAL_NORMALIZER = 0.75f;
        private readonly byte[] rawExpressions = new byte[NATURAL_EXPRESSIONS_COUNT * 4 + (8 * 2 * 4)];
        private readonly float[] expressions = new float[NATURAL_EXPRESSIONS_COUNT + (8 * 2)];

        private double pitch_L, yaw_L, pitch_R, yaw_R; // Eye rotations

        #region RESONITE VARIABLES
        private InputInterface _input;
        public int UpdateOrder => 100;
        private Mouth _mouth;
        private Eyes _eyes;
        #endregion

        #region ALXR VARIABLES
        private Process _alxrProcess;

        bool IsALXRRunning
        {
            get
            {
                Process[] pname = Process.GetProcessesByName("alxr-client");
                UniLog.Log($"ALXR processes: {pname.Length}");
                return pname.Length > 0;
            }
        }

        bool StartALXR()
        {
            // Attempt to start ALXR.
            _alxrProcess = new Process();
            string resonitePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            resonitePath = System.IO.Path.GetDirectoryName(resonitePath);

            _alxrProcess.StartInfo.FileName = resonitePath + "/alxr_client_windows/alxr-client.exe";
            _alxrProcess.StartInfo.Arguments = "--no-alvr-server --no-bindings";

            UniLog.Log($"Starting ALXR at: {_alxrProcess.StartInfo.FileName}");

            return _alxrProcess.Start();
        }
        #endregion

        public void Initialize(string ipconfig)
        {
            localAddr = IPAddress.Parse(ipconfig);

            cancellationTokenSource = new CancellationTokenSource();

            tcpThread = new Thread(Update);
            tcpThread.Start();
        }

        private bool ConnectToTCP()
        {
            client = new TcpClient();
            UniLog.Log($"Trying to establish a Quest Pro connection at {localAddr}:{DEFAULT_PORT}...");

            client.Connect(localAddr, DEFAULT_PORT);

            if (client.Connected)
            {
                UniLog.Log("Connected to Quest Pro!");

                stream = client.GetStream();
                connected = true;

                return true;
            }
            else
            {
                connected = false;
                return false;
            }
        }

        public void Update()
        {
            while (!cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    if (!connected)
                    {
                        UniLog.Log("Not connected.  Attempting connection.");
                        if (!IsALXRRunning)
                        {
                            StartALXR();
                            Thread.Sleep(2000);
                        }

                        ConnectToTCP();

                        if (!connected)
                            Thread.Sleep(1000);
                    }


                    // If the connection was unsuccessful, wait a bit and try again
                    if (stream == null)
                    {
                        connected = false;
                    }

                    if (!stream.CanRead)
                    {
                        connected = false;
                    }

                    if (connected)
                    {
                        int offset = 0;
                        int readBytes;
                        do
                        {
                            readBytes = stream.Read(rawExpressions, offset, rawExpressions.Length - offset);
                            offset += readBytes;
                        }
                        while (readBytes > 0 && offset < rawExpressions.Length);

                        if (offset < rawExpressions.Length)
                        {
                            UniLog.Warning("End of stream! Reconnecting...");
                            Thread.Sleep(1000);
                            connected = false;
                            try
                            {
                                stream.Close();
                            }
                            catch (SocketException e)
                            {
                                UniLog.Error(e.Message);
                                Thread.Sleep(1000);
                            }
                        }

                        // We receive information from the stream as a byte array 63*4 bytes long, since floats are 32 bits long and we have 63 expressions.
                        // We then need to convert these bytes to a floats. I've opted to use Buffer.BlockCopy instead of BitConverter.ToSingle, since it's faster.
                        // Future note: Testing this against Array.Copy() doesn't seem to show any difference in performance, so I'll stick to this
                        Buffer.BlockCopy(rawExpressions, 0, expressions, 0, NATURAL_EXPRESSIONS_COUNT * 4 + (8 * 2 * 4));

                        // Preprocess our expressions per Meta's Documentation
                        PrepareUpdate();
                    }
                }
                catch (Exception)
                {
                    // Do some cleanup prior to reconnecting.
                    connected = false;

                    if (stream != null)
                    {
                        stream.Close();
                        stream.Dispose();
                    }

                    if (client != null)
                    {
                        client.Close();
                        client.Dispose();
                    }

                    Thread.Sleep(1000);
                }

            }
        }

        private void PrepareUpdate()
        {
            // Eye Expressions

            double q_x = expressions[FBExpression.LeftRot_x];
            double q_y = expressions[FBExpression.LeftRot_y];
            double q_z = expressions[FBExpression.LeftRot_z];
            double q_w = expressions[FBExpression.LeftRot_w];

            double yaw = Math.Atan2(2.0 * (q_y * q_z + q_w * q_x), q_w * q_w - q_x * q_x - q_y * q_y + q_z * q_z);
            double pitch = Math.Asin(-2.0 * (q_x * q_z - q_w * q_y));
            // Not needed for eye tracking
            // double roll = Math.Atan2(2.0 * (q_x * q_y + q_w * q_z), q_w * q_w + q_x * q_x - q_y * q_y - q_z * q_z);

            // From radians
            pitch_L = 180.0 / Math.PI * pitch;
            yaw_L = 180.0 / Math.PI * yaw;

            q_x = expressions[FBExpression.RightRot_x];
            q_y = expressions[FBExpression.RightRot_y];
            q_z = expressions[FBExpression.RightRot_z];
            q_w = expressions[FBExpression.RightRot_w];

            yaw = Math.Atan2(2.0 * (q_y * q_z + q_w * q_x), q_w * q_w - q_x * q_x - q_y * q_y + q_z * q_z);
            pitch = Math.Asin(-2.0 * (q_x * q_z - q_w * q_y));

            // From radians
            pitch_R = 180.0 / Math.PI * pitch;
            yaw_R = 180.0 / Math.PI * yaw;

            // Face Expressions

            // Eyelid edge case, eyes are actually closed now
            if (expressions[FBExpression.Eyes_Look_Down_L] == expressions[FBExpression.Eyes_Look_Up_L] && expressions[FBExpression.Eyes_Closed_L] > 0.25f)
            {
                expressions[FBExpression.Eyes_Closed_L] = 0; // 0.9f - (expressions[FBExpression.Lid_Tightener_L] * 3);
            }
            else
            {
                expressions[FBExpression.Eyes_Closed_L] = 0.9f - ((expressions[FBExpression.Eyes_Closed_L] * 3) / (1 + expressions[FBExpression.Eyes_Look_Down_L] * 3));
            }

            // Another eyelid edge case
            if (expressions[FBExpression.Eyes_Look_Down_R] == expressions[FBExpression.Eyes_Look_Up_R] && expressions[FBExpression.Eyes_Closed_R] > 0.25f)
            {
                expressions[FBExpression.Eyes_Closed_R] = 0; // 0.9f - (expressions[FBExpression.Lid_Tightener_R] * 3);
            }
            else
            {
                expressions[FBExpression.Eyes_Closed_R] = 0.9f - ((expressions[FBExpression.Eyes_Closed_R] * 3) / (1 + expressions[FBExpression.Eyes_Look_Down_R] * 3));
            }

            // Hack: turn rots to looks
            // Yitch = 29(left)-- > -29(right)
            // Yaw = -27(down)-- > 27(up)

            if (pitch_L > 0)
            {
                expressions[FBExpression.Eyes_Look_Left_L] = Math.Min(1, (float)(pitch_L / 29.0)) * SRANIPAL_NORMALIZER;
                expressions[FBExpression.Eyes_Look_Right_L] = 0;
            }
            else
            {
                expressions[FBExpression.Eyes_Look_Left_L] = 0;
                expressions[FBExpression.Eyes_Look_Right_L] = Math.Min(1, (float)((-pitch_L) / 29.0)) * SRANIPAL_NORMALIZER;
            }

            if (yaw_L > 0)
            {
                expressions[FBExpression.Eyes_Look_Up_L] = Math.Min(1, (float)(yaw_L / 27.0)) * SRANIPAL_NORMALIZER;
                expressions[FBExpression.Eyes_Look_Down_L] = 0;
            }
            else
            {
                expressions[FBExpression.Eyes_Look_Up_L] = 0;
                expressions[FBExpression.Eyes_Look_Down_L] = Math.Min(1, (float)((-yaw_L) / 27.0)) * SRANIPAL_NORMALIZER;
            }


            if (pitch_R > 0)
            {
                expressions[FBExpression.Eyes_Look_Left_R] = Math.Min(1, (float)(pitch_R / 29.0)) * SRANIPAL_NORMALIZER;
                expressions[FBExpression.Eyes_Look_Right_R] = 0;
            }
            else
            {
                expressions[FBExpression.Eyes_Look_Left_R] = 0;
                expressions[FBExpression.Eyes_Look_Right_R] = Math.Min(1, (float)((-pitch_R) / 29.0)) * SRANIPAL_NORMALIZER;
            }

            if (yaw_R > 0)
            {
                expressions[FBExpression.Eyes_Look_Up_R] = Math.Min(1, (float)(yaw_R / 27.0)) * SRANIPAL_NORMALIZER;
                expressions[FBExpression.Eyes_Look_Down_R] = 0;
            }
            else
            {
                expressions[FBExpression.Eyes_Look_Up_R] = 0;
                expressions[FBExpression.Eyes_Look_Down_R] = Math.Min(1, (float)((-yaw_R) / 27.0)) * SRANIPAL_NORMALIZER;
            }
        }

        public void Teardown()
        {
            cancellationTokenSource.Cancel();

            if (tcpThread != null)
                tcpThread.Abort();

            cancellationTokenSource.Dispose();

            if (stream != null)
            {
                stream.Close();
                stream.Dispose();
            }

            if (client != null)
            {
                client.Close();
                client.Dispose();
            }

            if (_alxrProcess != null)
            {
                _alxrProcess.Close();
            }
        }

        bool IsValid(float3 value) => IsValid(value.x) && IsValid(value.y) && IsValid(value.z);
        bool IsValid(floatQ value) => IsValid(value.x) && IsValid(value.y) && IsValid(value.z) && IsValid(value.w) && InRange(value.x, new float2(1, -1)) && InRange(value.y, new float2(1, -1)) && InRange(value.z, new float2(1, -1)) && InRange(value.w, new float2(1, -1));

        bool IsValid(float value) => !float.IsInfinity(value) && !float.IsNaN(value);

        bool InRange(float value, float2 range) => (value <= range.x && value >= range.y);

        public struct EyeGazeData
        {
            public bool isValid;
            public float3 position;
            public floatQ rotation;
            public float open;
            public float squeeze;
            public float wide;
            public float gazeConfidence;
        }

        public EyeGazeData GetEyeData(FBEye fbEye)
        {
            EyeGazeData eyeRet = new EyeGazeData();
            switch (fbEye)
            {
                case FBEye.Left:
                    eyeRet.position = new float3(expressions[FBExpression.LeftPos_x], -expressions[FBExpression.LeftPos_y], expressions[FBExpression.LeftPos_z]);
                    eyeRet.rotation = new floatQ(-expressions[FBExpression.LeftRot_x], -expressions[FBExpression.LeftRot_y], -expressions[FBExpression.LeftRot_z], expressions[FBExpression.LeftRot_w]);
                    eyeRet.open = MathX.Max(0, expressions[FBExpression.Eyes_Closed_L]);
                    eyeRet.squeeze = expressions[FBExpression.Lid_Tightener_L];
                    eyeRet.wide = expressions[FBExpression.Upper_Lid_Raiser_L];
                    eyeRet.isValid = IsValid(eyeRet.position);
                    return eyeRet;
                case FBEye.Right:
                    eyeRet.position = new float3(expressions[FBExpression.RightPos_x], -expressions[FBExpression.RightPos_y], expressions[FBExpression.RightPos_z]);
                    eyeRet.rotation = new floatQ(-expressions[FBExpression.LeftRot_x], -expressions[FBExpression.LeftRot_y], -expressions[FBExpression.LeftRot_z], expressions[FBExpression.RightRot_w]);
                    eyeRet.open = MathX.Max(0, expressions[FBExpression.Eyes_Closed_R]);
                    eyeRet.squeeze = expressions[FBExpression.Lid_Tightener_R];
                    eyeRet.wide = expressions[FBExpression.Upper_Lid_Raiser_R];
                    eyeRet.isValid = IsValid(eyeRet.position);
                    return eyeRet;
                default:
                    throw new Exception($"Invalid eye argument: {fbEye}");
            }
        }

        public void GetEyeExpressions(FBEye fbEye, FrooxEngine.Eye frooxEye)
        {
            frooxEye.PupilDiameter = 0.004f;

            switch (fbEye)
            {
                case FBEye.Left:
                    frooxEye.UpdateWithRotation(new floatQ(-expressions[FBExpression.LeftRot_x], -expressions[FBExpression.LeftRot_z], -expressions[FBExpression.LeftRot_y], expressions[FBExpression.LeftRot_w]));
                    frooxEye.RawPosition = new float3(expressions[FBExpression.LeftPos_x], expressions[FBExpression.LeftPos_y], expressions[FBExpression.LeftPos_z]);
                    frooxEye.Openness = MathX.Max(0, expressions[FBExpression.Eyes_Closed_L]);
                    frooxEye.Squeeze = expressions[FBExpression.Lid_Tightener_L];
                    frooxEye.Widen = expressions[FBExpression.Upper_Lid_Raiser_L];
                    frooxEye.Frown = expressions[FBExpression.Lip_Corner_Puller_L] - expressions[FBExpression.Lip_Corner_Depressor_L];
                    break;
                case FBEye.Right:
                    frooxEye.UpdateWithRotation(new floatQ(-expressions[FBExpression.RightRot_x], -expressions[FBExpression.RightRot_z], -expressions[FBExpression.RightRot_y], expressions[FBExpression.RightRot_w]));
                    frooxEye.RawPosition = new float3(expressions[FBExpression.RightPos_x], expressions[FBExpression.RightPos_y], expressions[FBExpression.RightPos_z]);
                    frooxEye.Openness = MathX.Max(0, expressions[FBExpression.Eyes_Closed_R]);
                    frooxEye.Squeeze = expressions[FBExpression.Lid_Tightener_R];
                    frooxEye.Widen = expressions[FBExpression.Upper_Lid_Raiser_R];
                    frooxEye.Frown = expressions[FBExpression.Lip_Corner_Puller_R] - expressions[FBExpression.Lip_Corner_Depressor_R];
                    break;
                case FBEye.Combined:
                    frooxEye.UpdateWithRotation(MathX.Slerp(new floatQ(expressions[FBExpression.LeftRot_x], expressions[FBExpression.LeftRot_y], expressions[FBExpression.LeftRot_z], expressions[FBExpression.LeftRot_w]), new floatQ(expressions[FBExpression.RightRot_x], expressions[FBExpression.RightRot_y], expressions[FBExpression.RightRot_z], expressions[FBExpression.RightRot_w]), 0.5f));
                    frooxEye.RawPosition = MathX.Average(new float3(expressions[FBExpression.LeftPos_x], expressions[FBExpression.LeftPos_z], expressions[FBExpression.LeftPos_y]), new float3(expressions[FBExpression.RightPos_x], expressions[FBExpression.RightPos_z], expressions[FBExpression.RightPos_y]));
                    frooxEye.Openness = MathX.Max(0, expressions[FBExpression.Eyes_Closed_R] + expressions[FBExpression.Eyes_Closed_R]) / 2.0f;
                    frooxEye.Squeeze = (expressions[FBExpression.Lid_Tightener_R] + expressions[FBExpression.Lid_Tightener_R]) / 2.0f;
                    frooxEye.Widen = (expressions[FBExpression.Upper_Lid_Raiser_R] + expressions[FBExpression.Upper_Lid_Raiser_R]) / 2.0f;
                    frooxEye.Frown = (expressions[FBExpression.Lip_Corner_Puller_R] - expressions[FBExpression.Lip_Corner_Depressor_R]) + (expressions[FBExpression.Lip_Corner_Puller_L] - expressions[FBExpression.Lip_Corner_Depressor_L]) / 2.0f;
                    break;
            }

            frooxEye.IsTracking = IsValid(frooxEye.RawPosition);
            frooxEye.IsTracking = IsValid(frooxEye.Direction);
            frooxEye.IsTracking = IsValid(frooxEye.Openness);
        }

        #region IINPUTDRIVER METHODS
        /// <summary>
        /// Registers the eye and lip tracking devices with Resonite.
        /// </summary>
        /// <param name="list"></param>
        public void CollectDeviceInfos(DataTreeList list)
        {
            var eyeDataTreeDictionary = new DataTreeDictionary();
            eyeDataTreeDictionary.Add("Name", "Quest Pro Eye Tracking");
            eyeDataTreeDictionary.Add("Type", "Eye Tracking");
            eyeDataTreeDictionary.Add("Model", "Quest Pro");
            list.Add(eyeDataTreeDictionary);

            var mouthDataTreeDictionary = new DataTreeDictionary();
            mouthDataTreeDictionary.Add("Name", "Quest Pro Face Tracking");
            mouthDataTreeDictionary.Add("Type", "Lip Tracking");
            mouthDataTreeDictionary.Add("Model", "Quest Pro");
            list.Add(mouthDataTreeDictionary);
        }

        /// <summary>
        /// Sets up the input interfaces for the eyes and mouth data.
        /// </summary>
        /// <param name="inputInterface"></param>
        public void RegisterInputs(InputInterface inputInterface)
        {
            _input = inputInterface;
            _eyes = new Eyes(_input, "Quest Pro Eye Tracking");
            _mouth = new Mouth(_input, "Quest Pro Face Tracking");
        }

        /// <summary>
        /// Gets called every frame to update any inputs.
        /// </summary>
        /// <param name="deltaTime"></param>
        public void UpdateInputs(float deltaTime)
        {
            UpdateMouth(deltaTime);
            UpdateEyes(deltaTime);
        }

        void UpdateEye(Eye eye, EyeGazeData data)
        {
            bool _isValid = IsValid(data.open);
            _isValid &= IsValid(data.position);
            _isValid &= IsValid(data.wide);
            _isValid &= IsValid(data.squeeze);
            _isValid &= IsValid(data.rotation);
            _isValid &= eye.IsTracking;

            eye.IsTracking = _isValid;

            if (eye.IsTracking)
            {
                eye.UpdateWithRotation(MathX.Slerp(floatQ.Identity, data.rotation, QuestProMod.EyeMoveMult));
                eye.Openness = MathX.Pow(MathX.FilterInvalid(data.open, 0.0f), QuestProMod.EyeOpenExponent);
                eye.Widen = data.wide * QuestProMod.EyeWideMult;
            }
        }

        /// <summary>
        /// Updates our eye tracking data.
        /// </summary>
        /// <param name="deltaTime"></param>
        void UpdateEyes(float deltaTime)
        {
            _eyes.IsEyeTrackingActive = _input.VR_Active;

            _eyes.LeftEye.IsTracking = _input.VR_Active;

            var leftEyeData = QuestProMod.qpm.GetEyeData(FBEye.Left);
            var rightEyeData = QuestProMod.qpm.GetEyeData(FBEye.Right);

            _eyes.LeftEye.IsTracking = leftEyeData.isValid;
            _eyes.LeftEye.RawPosition = leftEyeData.position;
            _eyes.LeftEye.PupilDiameter = 0.004f;
            _eyes.LeftEye.Squeeze = leftEyeData.squeeze;
            _eyes.LeftEye.Frown = expressions[FBExpression.Lip_Corner_Puller_L] - expressions[FBExpression.Lip_Corner_Depressor_L] * QuestProMod.EyeExpressionMult;

            UpdateEye(_eyes.LeftEye, leftEyeData);

            _eyes.RightEye.IsTracking = rightEyeData.isValid;
            _eyes.RightEye.RawPosition = rightEyeData.position;
            _eyes.RightEye.PupilDiameter = 0.004f;
            _eyes.RightEye.Squeeze = rightEyeData.squeeze;
            _eyes.RightEye.Frown = expressions[FBExpression.Lip_Corner_Puller_R] - expressions[FBExpression.Lip_Corner_Depressor_R] * QuestProMod.EyeExpressionMult;

            UpdateEye(_eyes.RightEye, rightEyeData);

            if (_eyes.LeftEye.IsTracking || _eyes.RightEye.IsTracking && (!_eyes.LeftEye.IsTracking || !_eyes.RightEye.IsTracking))
            {
                if (_eyes.LeftEye.IsTracking)
                {
                    _eyes.CombinedEye.RawPosition = _eyes.LeftEye.RawPosition;
                    _eyes.CombinedEye.UpdateWithRotation(_eyes.LeftEye.RawRotation);
                }
                else
                {
                    _eyes.CombinedEye.RawPosition = _eyes.RightEye.RawPosition;
                    _eyes.CombinedEye.UpdateWithRotation(_eyes.RightEye.RawRotation);
                }
                _eyes.CombinedEye.IsTracking = true;
            }
            else
            {
                _eyes.CombinedEye.IsTracking = false;
            }

            _eyes.CombinedEye.IsTracking = _eyes.LeftEye.IsTracking || _eyes.RightEye.IsTracking;
            _eyes.CombinedEye.RawPosition = (_eyes.LeftEye.RawPosition + _eyes.RightEye.RawPosition) * 0.5f;
            _eyes.CombinedEye.UpdateWithRotation(MathX.Slerp(_eyes.LeftEye.RawRotation, _eyes.RightEye.RawRotation, 0.5f));
            _eyes.CombinedEye.PupilDiameter = 0.004f;

            _eyes.LeftEye.Openness = MathX.Pow(_eyes.LeftEye.Openness, QuestProMod.EyeOpenExponent);
            _eyes.RightEye.Openness = MathX.Pow(_eyes.RightEye.Openness, QuestProMod.EyeOpenExponent);

            _eyes.ComputeCombinedEyeParameters();
            _eyes.ConvergenceDistance = 0f;
            _eyes.Timestamp += deltaTime;
            _eyes.FinishUpdate();
        }

        /// <summary>
        /// Updates our mouth tracking data.
        /// </summary>
        /// <param name="deltaTime"></param>
        void UpdateMouth(float deltaTime)
        {
            _mouth.IsDeviceActive = Engine.Current.InputInterface.VR_Active;
            _mouth.IsTracking = Engine.Current.InputInterface.VR_Active;

            _mouth.JawOpen = expressions[FBExpression.Jaw_Drop] - expressions[FBExpression.Lips_Toward];

            var jawHorizontal = expressions[FBExpression.Jaw_Sideways_Right] - expressions[FBExpression.Jaw_Sideways_Left];
            var jawForward = expressions[FBExpression.Jaw_Thrust];
            var jawDown = expressions[FBExpression.Jaw_Drop];

            _mouth.Jaw = new float3(
                jawHorizontal,
                jawDown,
                jawForward
            );

            _mouth.LipUpperLeftRaise = expressions[FBExpression.Upper_Lip_Raiser_L];
            _mouth.LipUpperRightRaise = expressions[FBExpression.Upper_Lip_Raiser_R];
            _mouth.LipLowerLeftRaise = expressions[FBExpression.Lower_Lip_Depressor_L];
            _mouth.LipLowerRightRaise = expressions[FBExpression.Lower_Lip_Depressor_R];

            _mouth.LipUpperHorizontal = expressions[FBExpression.Mouth_Right] - expressions[FBExpression.Mouth_Left];
            _mouth.LipLowerHorizontal = expressions[FBExpression.Mouth_Right] - expressions[FBExpression.Mouth_Left];

            _mouth.MouthLeftSmileFrown = expressions[FBExpression.Lip_Corner_Puller_L] - expressions[FBExpression.Lip_Corner_Depressor_L];
            _mouth.MouthRightSmileFrown = expressions[FBExpression.Lip_Corner_Puller_R] - expressions[FBExpression.Lip_Corner_Depressor_R];

            _mouth.MouthPout = expressions[FBExpression.Lip_Pucker_L] + expressions[FBExpression.Lip_Pucker_R];

            _mouth.LipTopOverturn = expressions[FBExpression.Lip_Funneler_RT] + expressions[FBExpression.Lip_Funneler_LT];
            _mouth.LipBottomOverturn = expressions[FBExpression.Lip_Funneler_RB] + expressions[FBExpression.Lip_Funneler_LB];

            _mouth.LipTopOverUnder = -(expressions[FBExpression.Lip_Suck_RT] + expressions[FBExpression.Lip_Suck_LT]);
            _mouth.LipBottomOverUnder = expressions[FBExpression.Chin_Raiser_B] - (expressions[FBExpression.Lip_Suck_RB] + expressions[FBExpression.Lip_Suck_LB]);

            _mouth.CheekLeftPuffSuck = expressions[FBExpression.Cheek_Puff_L];
            _mouth.CheekRightPuffSuck = expressions[FBExpression.Cheek_Puff_R];

            _mouth.CheekLeftPuffSuck -= expressions[FBExpression.Cheek_Suck_L];
            _mouth.CheekRightPuffSuck -= expressions[FBExpression.Cheek_Suck_R];

        }

        #endregion
        public enum FBEye
        {
            Left,
            Right,
            Combined
        }
    }
}
