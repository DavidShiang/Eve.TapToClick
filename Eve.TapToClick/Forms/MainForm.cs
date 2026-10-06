using Eve.TapToClick.Controls;
using Eve.TapToClick.Models;
using Eve.TapToClick.NativeInterop;
using Eve.TapToClick.Utilities;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Eve.TapToClick.Forms
{
    public partial class MainForm : Form
    {
        private AppConfiguration config;
        private TouchpadWatcher touchpadWatcher;
        private ActiveContactDisplay[] activeContactDisplays;
        private DateTime?[] lastActiveContactUiUpdates;

        private bool initialized = false;

        private TapData currentTap;
        private TapData previousTap;

        // Win32 消息及电源/设备通知常量
        private const int WM_POWERBROADCAST = 0x0218;
        private const int WM_INPUT_DEVICE_CHANGE = 0x02FE;

        private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        private const int PBT_APMRESUMESUSPEND = 0x0007;
        private const int PBT_APMSUSPEND = 0x0004;
        private const int GIDC_ARRIVAL = 1;

        // 修正为 IntPtr 匹配项目的 MouseInput.ExtraInfo 类型
        private static readonly IntPtr TAP_INPUT_EXTRA_INFO = new IntPtr(0x544150); // "TAP"

        public MainForm()
        {
            InitializeComponent();

            // 1. 加载配置单例
            config = AppConfiguration.Instance;

            // 2. 初始化 TouchpadWatcher 并绑定事件
            touchpadWatcher = new TouchpadWatcher();
            touchpadWatcher.MinimumDetectionPressure = config.DetectionThreshold;
            touchpadWatcher.ContactStart += HandleContactStart;
            touchpadWatcher.ContactUpdate += HandleContactUpdate;
            touchpadWatcher.ContactEnd += HandleContactEnd;

            // 3. 初始化 UI 节流时间戳记录数组，节约高频触控下的 CPU 资源
            lastActiveContactUiUpdates = new DateTime?[Constants.MaxContacts];

            // 4. UI 触控点显示控件映射
            activeContactDisplays = new ActiveContactDisplay[]
            {
                activeContactDisplay1,
                activeContactDisplay2,
                activeContactDisplay3,
                activeContactDisplay4,
                activeContactDisplay5
            };

            // 5. 注册系统电源模式改变通知（作为休眠唤醒的双重保障）
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            // 检查管理员权限警告（解决 UIPI 导致在高权限窗口点击失效问题）
            CheckAdminPrivileges();

            // 注册 Raw Input 设备监听
            RegisterRawInput();

            // 加载配置参数到界面
            LoadConfigValues();

            // 禁用应用配置按钮直到检测到修改
            applyConfigButton.Enabled = false;

            // 检查开机自启任务
            if (AutoRun.StartupTaskExists())
                startupCheckbox.Checked = true;

            initialized = true;
        }

        /// <summary>
        /// 注册/重新注册 Raw Input 设备 (含 DevNotify 选项)
        /// </summary>
        private void RegisterRawInput()
        {
            try
            {
                // 解决 CS0117 错误：用数值强制转换 RawInputDeviceFlags.DevNotify (0x00002000)
                RawInputDeviceFlags flags = RawInputDeviceFlags.InputSink | (RawInputDeviceFlags)0x00002000;

                User32.RegisterRawInputDevices(new RawInputDevice[]
                {
                    new RawInputDevice
                    {
                        UsagePage = Constants.TargetDeviceUsage.UsagePage,
                        Usage = Constants.TargetDeviceUsage.Usage,
                        Flags = flags,
                        WindowHandle = this.Handle
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RegisterRawInput Failed]: {ex.Message}");
            }
        }

        /// <summary>
        /// 状态机重置：在睡眠、唤醒、设备变更或数据异常时清除残留在内存中的触控数据，防“假点击”
        /// </summary>
        private void ResetTouchState()
        {
            currentTap = null;

            // 如果处于非最小化状态，清空界面活动触点控件状态
            if (WindowState != FormWindowState.Minimized && activeContactDisplays != null)
            {
                for (int i = 0; i < activeContactDisplays.Length; i++)
                {
                    if (activeContactDisplays[i] != null)
                    {
                        activeContactDisplays[i].Active = false;
                        activeContactDisplays[i].Pressure = 0;
                        activeContactDisplays[i].X = 0;
                        activeContactDisplays[i].Y = 0;
                    }
                    lastActiveContactUiUpdates[i] = null;
                }
            }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            switch (e.Mode)
            {
                case PowerModes.Suspend:
                    // 系统准备睡眠时提前清空触控状态
                    ResetTouchState();
                    break;

                case PowerModes.Resume:
                    // 系统休眠唤醒后重置状态并强行重新注册设备句柄
                    ResetTouchState();
                    RegisterRawInput();
                    break;
            }
        }

        private void CheckAdminPrivileges()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                bool isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
                if (!isAdmin)
                {
                    System.Diagnostics.Debug.WriteLine("[Warning]: Running without Administrator rights. UIPI may block simulated inputs on high-privilege windows.");
                }
            }
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            if (Environment.GetCommandLineArgs().Any(s => s.ToLower() == "--minimize"))
                WindowState = FormWindowState.Minimized;
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            switch (m.Msg)
            {
                case (int)WindowMessage.WM_INPUT:
                    try
                    {
                        touchpadWatcher.HandleInputMessage(ref m);
                    }
                    catch (Exception ex)
                    {
                        // 防护：防止休眠唤醒后非法的 LParam 引起指针越界引发全局崩溃
                        System.Diagnostics.Debug.WriteLine($"[WM_INPUT Error]: {ex.Message}");
                        ResetTouchState();
                    }
                    break;

                case WM_POWERBROADCAST:
                    int wp = m.WParam.ToInt32();
                    if (wp == PBT_APMRESUMEAUTOMATIC || wp == PBT_APMRESUMESUSPEND)
                    {
                        ResetTouchState();
                        RegisterRawInput();
                    }
                    else if (wp == PBT_APMSUSPEND)
                    {
                        ResetTouchState();
                    }
                    break;

                case WM_INPUT_DEVICE_CHANGE:
                    // 硬件重新连接/重新初始化（唤醒枚举阶段）
                    if (m.WParam.ToInt32() == GIDC_ARRIVAL)
                    {
                        ResetTouchState();
                        RegisterRawInput();
                    }
                    break;
            }
        }

        private void HandleContactStart(object sender, TouchpadEventArgs e)
        {
            ProcessActiveContact(e);
        }

        private void HandleContactUpdate(object sender, TouchpadEventArgs e)
        {
            ProcessActiveContact(e);
        }

        private void ProcessActiveContact(TouchpadEventArgs eventArgs)
        {
            // 防护：若长时间未结束（例如超过最大超时时间2倍），清除旧的“僵尸”Tap，防止状态卡死
            if (currentTap != null && (DateTime.Now - currentTap.Start).TotalMilliseconds > config.MaxTapMilliseconds * 2)
            {
                currentTap = null;
            }

            // 如果当前不在活动 Tap 中，实例化新的 TapData
            if (currentTap == null)
                currentTap = new TapData(Constants.MaxContacts);

            bool wasActive = currentTap.InstantaneousActiveContacts[eventArgs.ContactIndex];
            currentTap.InstantaneousActiveContacts[eventArgs.ContactIndex] = true;

            if (wasActive)
            {
                uint previousX = currentTap.PreviousXValues[eventArgs.ContactIndex];
                uint previousY = currentTap.PreviousYValues[eventArgs.ContactIndex];

                double positionDelta = Math.Sqrt(Math.Pow((double)eventArgs.X - previousX, 2) + Math.Pow((double)eventArgs.Y - previousY, 2));
                currentTap.TotalContactDistances[eventArgs.ContactIndex] += positionDelta;
            }

            // 性能优化：替代 LINQ Count()，用简单的非堆分配循环统计活动触点
            int activeCount = 0;
            for (int i = 0; i < currentTap.InstantaneousActiveContacts.Length; i++)
            {
                if (currentTap.InstantaneousActiveContacts[i])
                    activeCount++;
            }

            currentTap.MaximumActiveContacts = Math.Max(activeCount, currentTap.MaximumActiveContacts);
            currentTap.MaximumPressure = Math.Max(currentTap.MaximumPressure, eventArgs.Pressure);

            currentTap.PreviousXValues[eventArgs.ContactIndex] = eventArgs.X;
            currentTap.PreviousYValues[eventArgs.ContactIndex] = eventArgs.Y;

            if (eventArgs.Pressure >= config.TapTriggerThreshold)
            {
                currentTap.TapThresholdMet = true;
            }

            // 如果未最小化，限流更新 UI (50ms 阀值)
            if (WindowState != FormWindowState.Minimized &&
                (!lastActiveContactUiUpdates[eventArgs.ContactIndex].HasValue || (DateTime.Now - lastActiveContactUiUpdates[eventArgs.ContactIndex].Value).TotalMilliseconds >= 50))
            {
                ActiveContactDisplay contactDisplay = activeContactDisplays[eventArgs.ContactIndex];

                contactDisplay.Active = true;
                contactDisplay.Pressure = eventArgs.Pressure;
                contactDisplay.X = eventArgs.X;
                contactDisplay.Y = eventArgs.Y;

                lastActiveContactUiUpdates[eventArgs.ContactIndex] = DateTime.Now;
            }
        }

        private void HandleContactEnd(object sender, TouchpadEventArgs e)
        {
            if (currentTap == null)
                return;

            currentTap.InstantaneousActiveContacts[e.ContactIndex] = false;

            // 性能优化：无 GC 内存开销的活动触点计数
            int currentActiveContacts = 0;
            for (int i = 0; i < currentTap.InstantaneousActiveContacts.Length; i++)
            {
                if (currentTap.InstantaneousActiveContacts[i])
                    currentActiveContacts++;
            }

            // 所有触点均抬起，Tap 动作结束
            if (currentActiveContacts == 0)
            {
                DateTime tapEnd = DateTime.Now;

                // 验证该操作是否符合 Tap 标准（时间、压力、位移）
                if ((tapEnd - currentTap.Start).TotalMilliseconds <= config.MaxTapMilliseconds &&
                    currentTap.TapThresholdMet &&
                    currentTap.TotalContactDistances.Max() <= config.MaxTapDeltaPosition)
                {
                    int maxContacts = currentTap.MaximumActiveContacts;

                    // 异步派发 SendInput 注入点击，不阻塞 Raw Input 接收与 UI 线程
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        if (maxContacts == 1)
                        {
                            SendLeftClick();
                        }
                        else if (maxContacts == 2)
                        {
                            SendRightClick();
                        }
                        else if (maxContacts == 3)
                        {
                            SendMiddleClick();
                        }
                    });
                }

                previousTap = currentTap;
                currentTap = null;

                if (WindowState != FormWindowState.Minimized)
                {
                    previousMaxPressureLabel.Text = previousTap.MaximumPressure.ToString();
                    previousDurationLabel.Text = ((int)(tapEnd - previousTap.Start).TotalMilliseconds).ToString();
                    previousMaxDistanceLabel.Text = ((int)previousTap.TotalContactDistances.Max()).ToString();
                    previousContactCountLabel.Text = previousTap.MaximumActiveContacts.ToString();

                    previousMaxPressureLabel.ForeColor = previousTap.MaximumPressure >= config.TapTriggerThreshold
                        ? Color.DarkGreen
                        : Color.DarkRed;
                    previousDurationLabel.ForeColor = (tapEnd - previousTap.Start).TotalMilliseconds < config.MaxTapMilliseconds
                        ? Color.DarkGreen
                        : Color.DarkRed;
                    previousMaxDistanceLabel.ForeColor = previousTap.TotalContactDistances.Max() < config.MaxTapDeltaPosition
                        ? Color.DarkGreen
                        : Color.DarkRed;
                    previousContactCountLabel.ForeColor = previousTap.MaximumActiveContacts >= 1 && previousTap.MaximumActiveContacts <= 3
                        ? Color.Green
                        : Color.DarkRed;
                }
            }

            if (WindowState != FormWindowState.Minimized)
            {
                ActiveContactDisplay contactDisplay = activeContactDisplays[e.ContactIndex];

                contactDisplay.Active = false;
                contactDisplay.Pressure = 0;
                contactDisplay.X = 0;
                contactDisplay.Y = 0;
            }
        }

        private void SendLeftClick()
        {
            User32.SendInput(
                new Input
                {
                    Type = InputType.Mouse,
                    InputValue = new Input.InputUnion
                    {
                        MouseInput = new MouseInput
                        {
                            Flags = MouseInputFlag.LeftDown,
                            ExtraInfo = TAP_INPUT_EXTRA_INFO
                        }
                    }
                },
                new Input
                {
                    Type = InputType.Mouse,
                    InputValue = new Input.InputUnion
                    {
                        MouseInput = new MouseInput
                        {
                            Flags = MouseInputFlag.LeftUp,
                            ExtraInfo = TAP_INPUT_EXTRA_INFO
                        }
                    }
                }
            );
        }

        private void SendRightClick()
        {
            User32.SendInput(
                new Input
                {
                    Type = InputType.Mouse,
                    InputValue = new Input.InputUnion
                    {
                        MouseInput = new MouseInput
                        {
                            Flags = MouseInputFlag.RightDown,
                            ExtraInfo = TAP_INPUT_EXTRA_INFO
                        }
                    }
                },
                new Input
                {
                    Type = InputType.Mouse,
                    InputValue = new Input.InputUnion
                    {
                        MouseInput = new MouseInput
                        {
                            Flags = MouseInputFlag.RightUp,
                            ExtraInfo = TAP_INPUT_EXTRA_INFO
                        }
                    }
                }
            );
        }

        private void SendMiddleClick()
        {
            User32.SendInput(
                new Input
                {
                    Type = InputType.Mouse,
                    InputValue = new Input.InputUnion
                    {
                        MouseInput = new MouseInput
                        {
                            Flags = MouseInputFlag.MiddleDown,
                            ExtraInfo = TAP_INPUT_EXTRA_INFO
                        }
                    }
                },
                new Input
                {
                    Type = InputType.Mouse,
                    InputValue = new Input.InputUnion
                    {
                        MouseInput = new MouseInput
                        {
                            Flags = MouseInputFlag.MiddleUp,
                            ExtraInfo = TAP_INPUT_EXTRA_INFO
                        }
                    }
                }
            );
        }

        private void SaveConfigValues()
        {
            bool detectionThresholdParsed = uint.TryParse(detectionThresholdTextBox.Text, out uint detectionThreshold);
            bool triggerThresholdParsed = uint.TryParse(triggerThresholdTextBox.Text, out uint triggerThreshold);
            bool maxMillisecondsParsed = int.TryParse(maxTapMillisecondsTextBox.Text, out int maxMilliseconds);
            bool maxDistanceParsed = int.TryParse(maxTapDistanceTextBox.Text, out int maxDistance);

            if (!detectionThresholdParsed || !triggerThresholdParsed || !maxMillisecondsParsed || !maxDistanceParsed)
            {
                MessageBox.Show("Invalid value entered in configuration.");
                LoadConfigValues();
                return;
            }

            config.DetectionThreshold = detectionThreshold;
            config.TapTriggerThreshold = triggerThreshold;
            config.MaxTapMilliseconds = maxMilliseconds;
            config.MaxTapDeltaPosition = maxDistance;

            config.Save();
        }

        private void LoadConfigValues()
        {
            detectionThresholdTextBox.Text = config.DetectionThreshold.ToString();
            triggerThresholdTextBox.Text = config.TapTriggerThreshold.ToString();
            maxTapMillisecondsTextBox.Text = config.MaxTapMilliseconds.ToString();
            maxTapDistanceTextBox.Text = config.MaxTapDeltaPosition.ToString();
        }

        private void applyConfigButton_Click(object sender, EventArgs e)
        {
            SaveConfigValues();
            touchpadWatcher.MinimumDetectionPressure = config.DetectionThreshold;
            applyConfigButton.Enabled = false;
        }

        private void configTextBox_TextChanged(object sender, EventArgs e)
        {
            applyConfigButton.Enabled = true;
        }

        private void notifyIconConfigureMenuItem_Click(object sender, EventArgs e)
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void notifyIconExitMenuItem_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();
            }
        }

        private void notifyIcon_DoubleClick(object sender, EventArgs e)
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void startupCheckbox_CheckedChanged(object sender, EventArgs e)
        {
            if (!initialized)
                return;

            AutoRun.RemoveStartupTask();

            if (startupCheckbox.Checked)
            {
                AutoRun.AddStartupTask();
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 注销系统电源变化监听，防止内存泄露
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            base.OnFormClosed(e);
        }
    }
}