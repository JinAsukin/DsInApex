using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace DsInApex.Playnite
{
    /// <summary>
    /// Ds in Apex 的 Playnite 集成（P9）。
    ///
    /// <para>
    /// 迁移自上游 <c>playnite/ApexSenseBridge</c>（0.6.3）。<b>引擎层一个字没动</b> ——
    /// 改的全是插件宿主侧：部署定位（不再走注册表）、更新通道（不再发安装器）、
    /// 本地化（上游 UI 是硬编码法文，en_US.xaml 还是空文件）、品牌与版本。
    /// </para>
    ///
    /// <para>
    /// <b>会话互斥：</b>本插件<b>不自己造锁</b>，而是启动同一个 C++ 引擎 ——
    /// 引擎启动时会持有 <c>Local\ApexSenseBridge.ActiveSession.Owner.v1</c>，
    /// 于是 DIA 主程序与官方托盘都能感知到「已有会话」，不会互相抢手柄
    /// （见 <see cref="SessionOwnership"/>）。
    /// </para>
    /// </summary>
    public class DsInApexPlugin : GenericPlugin
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private const string UpdateNotificationId = "DsInApex_UpdateAvailable";

        private readonly object sessionLock = new object();
        private readonly DsInApexSettingsViewModel settings;
        private BridgeSession activeSession;
        private Guid activeGameId;

        /// <summary>
        /// 插件身份。<b>必须与 extension.yaml 的 Id 后缀、AssemblyInfo 的 Guid 三处一致。</b>
        /// </summary>
        public override Guid Id { get; } = Guid.Parse("e41b1737-6753-4b59-bc65-4fdd6a7df7f4");

        public DsInApexPlugin(IPlayniteAPI api) : base(api)
        {
            // ⚠️ 本地化必须先于任何会取文案的对象构造（设置 VM 的显示名就走 Loc）
            string language = null;
            try
            {
                language = api.ApplicationSettings == null ? null : api.ApplicationSettings.Language;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "读取 Playnite 语言设置失败，按英文基底加载。");
            }
            Loc.Initialize(PluginFolder, language);
            Logger.Info("本地化：" + Loc.LoadReport);

            settings = new DsInApexSettingsViewModel(this);
            Properties = new GenericPluginProperties
            {
                HasSettings = true
            };
        }

        /// <summary>插件所在目录（含 <c>Localization\</c> 与 <c>extension.yaml</c>）。</summary>
        internal static string PluginFolder
        {
            get
            {
                try
                {
                    string location = Assembly.GetExecutingAssembly().Location;
                    return string.IsNullOrWhiteSpace(location) ? string.Empty : Path.GetDirectoryName(location);
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        // ────────────────────────── 生命周期 ──────────────────────────

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            base.OnApplicationStarted(args);

            if (!settings.Settings.AutoCheckUpdates)
            {
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    // 延后 3 秒：别和 Playnite 自己的启动流程抢 I/O
                    await Task.Delay(3000).ConfigureAwait(false);

                    UpdateCheckResult result = await UpdateManager
                        .CheckForUpdateAsync(GetDeployedVersion())
                        .ConfigureAwait(false);

                    settings.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
                    settings.SaveNow();

                    if (!result.IsUpdateAvailable)
                    {
                        return;
                    }

                    Logger.Info("发现新版本：" + result.TagName);
                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        UpdateNotificationId,
                        Loc.Format("LOCDsInApex_NotifyUpdate", result.TagName),
                        NotificationType.Info,
                        () => ConfirmOpenReleasePage(result)));
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "后台更新检查失败（不影响使用）。");
                }
            });
        }

        /// <summary>
        /// 通知被点击 → 问一句要不要打开发布页。
        /// 便携版没有静默安装可做，所以这里只做「告知 + 引路」，绝不自己动用户的文件。
        /// </summary>
        private void ConfirmOpenReleasePage(UpdateCheckResult result)
        {
            try
            {
                MessageBoxResult choice = PlayniteApi.Dialogs.ShowMessage(
                    Loc.Format("LOCDsInApex_DlgUpdateBody", result.TagName),
                    Loc.Get("LOCDsInApex_DlgUpdateTitle"),
                    MessageBoxButton.YesNo);

                if (choice == MessageBoxResult.Yes)
                {
                    UpdateManager.OpenReleasePage(result.ReleaseUrl);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "更新提示对话框失败，直接打开发布页。");
                UpdateManager.OpenReleasePage(result.ReleaseUrl);
            }
        }

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            bool automatic;
            string detectionReason;
            GameBridgeProfile profile = settings.Settings.ResolveProfile(
                args.Game, out automatic, out detectionReason);
            if (profile == null)
            {
                return;
            }

            if (automatic)
            {
                Logger.Info(string.Format("自动为 {0} 选中配置档 {1}（依据：{2}）。",
                    args.Game.Name, profile.ProfileType, detectionReason));
            }

            lock (sessionLock)
            {
                if (activeSession != null)
                {
                    CancelStartup(args, Loc.Get("LOCDsInApex_ErrSessionAlreadyActive"));
                    return;
                }

                // 与 DIA 主程序 / 官方托盘互斥：引擎互斥体已被别人持有就别抢
                if (SessionOwnership.IsExternalSessionActive())
                {
                    string message = Loc.Get("LOCDsInApex_ErrExternalSessionActive") + " " +
                                     Loc.Get("LOCDsInApex_ErrExternalSessionActiveHint");
                    CancelStartup(args, message);
                    return;
                }

                string enginePath = ResolveEnginePath(settings.Settings);
                if (string.IsNullOrWhiteSpace(enginePath))
                {
                    CancelStartup(args, ResolveNotFoundMessage(settings.Settings));
                    return;
                }

                string arguments = BuildBridgeArguments(profile);
                Logger.Info(string.Format("启动桥接：{0} {1}", enginePath, arguments));

                string error;
                BridgeSession session = BridgeSession.TryStart(
                    enginePath,
                    arguments,
                    TimeSpan.FromSeconds(settings.Settings.InitializationTimeoutSeconds),
                    Logger,
                    out error);
                if (session == null)
                {
                    CancelStartup(args, error);
                    return;
                }

                activeSession = session;
                activeGameId = args.Game.Id;
                Logger.Info(string.Format("桥接就绪：{0}（{1}）。", args.Game.Name, args.Game.Id));
            }
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            StopSession(args.Game.Id, "game stopped");
        }

        public override void OnGameStartupCancelled(OnGameStartupCancelledEventArgs args)
        {
            StopSession(args.Game.Id, "game startup cancelled");
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            StopSession(null, "Playnite stopped");
        }

        // ────────────────────────── 游戏右键菜单 ──────────────────────────

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var games = args.Games == null ? new List<Game>() : args.Games.ToList();
            GameBridgeProfile configured = games.Count == 1
                ? settings.Settings.FindProfile(games[0].Id)
                : null;
            BridgeActivationMode activation = configured == null
                ? BridgeActivationMode.Automatic
                : configured.ActivationMode;
            TouchpadRemappingMode remapping = configured == null
                ? TouchpadRemappingMode.Automatic
                : configured.RemappingMode;
            int apexProfile = configured == null ? 0 : configured.ApexProfileSlot;

            string activationAutoLabel = Loc.Get("LOCDsInApex_ActivationAuto");
            string remappingAutoLabel = Loc.Get("LOCDsInApex_RemappingAuto");
            BridgeProfileType detectedProfile;
            string ignoredReason;
            if (games.Count == 1 && AutomaticProfileDetector.TryDetect(
                    games[0], out detectedProfile, out ignoredReason))
            {
                activationAutoLabel = Loc.Get("LOCDsInApex_ActivationAutoDetected");
                remappingAutoLabel = Loc.Get("LOCDsInApex_ActivationAuto") + " (" +
                    GameBridgeProfile.GetProfileDisplayName(detectedProfile) + ")";
            }

            string activationSection = "Ds in Apex|" + Loc.Get("LOCDsInApex_MenuActivation");
            string remappingSection = "Ds in Apex|" + Loc.Get("LOCDsInApex_MenuRemapping");
            string apexSection = "Ds in Apex|" + Loc.Get("LOCDsInApex_MenuApexProfile");

            yield return CreateMenuItem(activationSection,
                Mark(activation == BridgeActivationMode.Automatic) + activationAutoLabel,
                action => SetActivations(action.Games, BridgeActivationMode.Automatic));
            yield return CreateMenuItem(activationSection,
                Mark(activation == BridgeActivationMode.Enabled) + Loc.Get("LOCDsInApex_ActivationEnabled"),
                action => SetActivations(action.Games, BridgeActivationMode.Enabled));
            yield return CreateMenuItem(activationSection,
                Mark(activation == BridgeActivationMode.Disabled) + Loc.Get("LOCDsInApex_ActivationDisabled"),
                action => SetActivations(action.Games, BridgeActivationMode.Disabled));

            yield return CreateMenuItem(remappingSection,
                Mark(remapping == TouchpadRemappingMode.Automatic) + remappingAutoLabel,
                action => SetRemappings(action.Games, TouchpadRemappingMode.Automatic));
            yield return CreateMenuItem(remappingSection,
                Mark(remapping == TouchpadRemappingMode.None) + Loc.Get("LOCDsInApex_RemappingNone"),
                action => SetRemappings(action.Games, TouchpadRemappingMode.None));
            foreach (TouchpadRemappingMode choice in new[]
            {
                TouchpadRemappingMode.SpiderMan2,
                TouchpadRemappingMode.MilesMorales,
                TouchpadRemappingMode.GhostOfTsushima,
                TouchpadRemappingMode.Warframe
            })
            {
                TouchpadRemappingMode selectedChoice = choice;
                yield return CreateMenuItem(remappingSection,
                    Mark(remapping == selectedChoice) + GameBridgeProfile.GetRemappingDisplayName(selectedChoice),
                    action => SetRemappings(action.Games, selectedChoice));
            }

            yield return CreateMenuItem(apexSection,
                Mark(apexProfile == 0) + Loc.Get("LOCDsInApex_ApexProfileKeep"),
                action => SetApexProfiles(action.Games, 0));
            for (int slot = 1; slot <= 4; slot++)
            {
                int selectedSlot = slot;
                yield return CreateMenuItem(apexSection,
                    Mark(apexProfile == selectedSlot) + Loc.Format("LOCDsInApex_ApexProfileSlot", selectedSlot),
                    action => SetApexProfiles(action.Games, selectedSlot));
            }
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settings;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new DsInApexSettingsView();
        }

        // ────────────────────────── 供设置页 / 诊断调用 ──────────────────────────

        /// <summary>解析部署根目录（含 <c>DsInApex.exe</c>）。</summary>
        internal string ResolveDeployRoot(DsInApexSettings value)
        {
            return InstallLocator.ResolveDeployRoot(value == null ? null : value.DeployRootPath);
        }

        /// <summary>解析桥接引擎可执行文件。</summary>
        internal string ResolveEnginePath(DsInApexSettings value)
        {
            return InstallLocator.ResolveEngine(value == null ? null : value.DeployRootPath);
        }

        /// <summary>本机 DIA 版本：优先读部署目录 version.json，读不到退回插件程序集版本。</summary>
        internal string GetDeployedVersion()
        {
            try
            {
                string root = ResolveDeployRoot(settings == null ? null : settings.Settings);
                string version = UpdateManager.ReadDeployedVersion(root);
                return string.IsNullOrWhiteSpace(version) ? UpdateManager.PluginVersion : version;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "读取本机版本失败。");
                return UpdateManager.PluginVersion;
            }
        }

        internal void SaveSettings(DsInApexSettings value)
        {
            SavePluginSettings(value);
        }

        // ────────────────────────── 内部实现 ──────────────────────────

        /// <summary>
        /// 拼装 <c>bridge-triggers</c> 参数。
        /// <b>⚠️ 与 C++ 引擎（<c>engine/src/cli/BridgeCommand.cpp</c>）逐字对应，
        /// 参数顺序与拼写一个字符都不能动。</b>
        /// <c>--session-token</c> 由 <see cref="BridgeSession.TryStart"/> 追加，此处不要加。
        /// </summary>
        private string BuildBridgeArguments(GameBridgeProfile profile)
        {
            var arguments = new List<string> { "bridge-triggers" };

            arguments.Add("--touchpad-profile");
            arguments.Add(GameBridgeProfile.ToTouchpadProfileArgument(profile.ProfileType));

            if (profile.ApexProfileSlot >= 1 && profile.ApexProfileSlot <= 4)
            {
                arguments.Add("--apex-profile");
                arguments.Add(profile.ApexProfileSlot.ToString());
            }

            if (settings.Settings.EnableRumble)
            {
                arguments.Add("--rumble");
                arguments.Add("--haptic-threshold");
                arguments.Add(settings.Settings.HapticThresholdPercent.ToString());
            }

            // APEX 4 陀螺仪灵敏度（引擎 1.0.0-beta.10 起，上游 issue #10）。
            // ⚠️ 只在非默认值时才追加：100 等于引擎默认值，不传与传 100 等价，
            //    这样旧配置生成的命令行逐字不变（便于与历史日志对照）。
            // ⚠️ 必须夹紧：用户可在设置里手打任意数字，而引擎对越界值是
            //    【拒绝启动】而不是忽略 —— 那会让"启动游戏"直接失败。
            int gyro = ClampGyroPercent(settings.Settings.Apex4GyroStrengthPercent);
            if (gyro != 100)
            {
                arguments.Add("--apex4-gyro-strength");
                arguments.Add(gyro.ToString());
            }

            int gyroYaw = ClampGyroPercent(settings.Settings.Apex4GyroYawStrengthPercent);
            if (gyroYaw != 100)
            {
                arguments.Add("--apex4-gyro-yaw-strength");
                arguments.Add(gyroYaw.ToString());
            }

            return string.Join(" ", arguments);
        }

        /// <summary>把陀螺仪灵敏度夹到引擎允许的 25–400（区间见 C++ <c>BridgeOptions.cpp</c>）。</summary>
        private static int ClampGyroPercent(int value)
        {
            if (value < 25) return 25;
            if (value > 400) return 400;
            return value;
        }

        private string ResolveNotFoundMessage(DsInApexSettings value)
        {
            // 分两种失败：根本没找到部署根，还是找到了但引擎缺了 —— 报错要能指向病灶
            if (string.IsNullOrWhiteSpace(ResolveDeployRoot(value)))
            {
                return Loc.Get("LOCDsInApex_ErrDeployRootNotFound");
            }
            return Loc.Get("LOCDsInApex_ErrEngineNotFound");
        }

        private static string Mark(bool selected)
        {
            return selected ? "✓ " : "   ";
        }

        private static GameMenuItem CreateMenuItem(
            string section,
            string description,
            Action<GameMenuItemActionArgs> action)
        {
            return new GameMenuItem
            {
                MenuSection = section,
                Description = description,
                Action = action
            };
        }

        private void SetActivations(IEnumerable<Game> games, BridgeActivationMode activationMode)
        {
            var list = games == null ? new List<Game>() : games.ToList();
            foreach (Game game in list)
            {
                settings.Settings.SetActivation(game, activationMode);
            }
            settings.SaveNow();
            Logger.Info(string.Format("激活方式设为 {0}（{1} 个游戏）。", activationMode, list.Count));
        }

        private void SetRemappings(IEnumerable<Game> games, TouchpadRemappingMode remappingMode)
        {
            var list = games == null ? new List<Game>() : games.ToList();
            foreach (Game game in list)
            {
                settings.Settings.SetRemapping(game, remappingMode);
            }
            settings.SaveNow();
            Logger.Info(string.Format("触控板映射设为 {0}（{1} 个游戏）。", remappingMode, list.Count));
        }

        private void SetApexProfiles(IEnumerable<Game> games, int slot)
        {
            var list = games == null ? new List<Game>() : games.ToList();
            foreach (Game game in list)
            {
                settings.Settings.SetApexProfile(game, slot);
            }
            settings.SaveNow();
            Logger.Info(string.Format("APEX 板载配置设为 {0}（{1} 个游戏）。", slot, list.Count));
        }

        private void StopSession(Guid? gameId, string reason)
        {
            BridgeSession session;
            lock (sessionLock)
            {
                if (activeSession == null ||
                    (gameId.HasValue && activeGameId != gameId.Value))
                {
                    return;
                }

                session = activeSession;
                activeSession = null;
                activeGameId = Guid.Empty;
            }

            Logger.Info("停止桥接会话：" + reason + "。");
            // ⚠️ 必须用 StopAndEnsureExit 而不是 StopAndWait：协同超时后要有强制回收，
            // 否则孤儿引擎进程会攥住全局会话锁（上游 issue #15）。
            if (!session.StopAndEnsureExit(
                    BridgeSession.DefaultStopTimeout, BridgeSession.ForcedStopOnFailure))
            {
                Logger.Error("桥接会话在协同超时后未停止，已强制结束本次启动的子进程。");
            }
            session.Dispose();
        }

        private void CancelStartup(OnGameStartingEventArgs args, string message)
        {
            args.CancelStartup = true;
            Logger.Error("游戏启动被 Ds in Apex 取消：" + message);
            PlayniteApi.Dialogs.ShowErrorMessage(message, Loc.Get("LOCDsInApex_DlgTitle"));
        }
    }
}
