using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;

namespace DsInApex.Playnite
{
    /// <summary>触摸板手势配置档。</summary>
    public enum BridgeProfileType
    {
        StandardDualSense,
        SpiderMan2,
        MilesMorales,
        GhostOfTsushima,
        Warframe,
        Disabled
    }

    /// <summary>启用方式。</summary>
    public enum BridgeActivationMode
    {
        Automatic,
        Enabled,
        Disabled
    }

    /// <summary>触控板映射模式。</summary>
    public enum TouchpadRemappingMode
    {
        Automatic,
        None,
        SpiderMan2,
        MilesMorales,
        GhostOfTsushima,
        Warframe
    }

    /// <summary>单个游戏的手动覆盖配置。</summary>
    public class GameBridgeProfile : ObservableObject
    {
        private Guid gameId;
        private string gameName = string.Empty;
        private BridgeProfileType profileType;
        private int configurationVersion;
        private BridgeActivationMode activationMode;
        private TouchpadRemappingMode remappingMode;
        private int apexProfileSlot;

        public Guid GameId { get { return gameId; } set { SetValue(ref gameId, value); } }

        public string GameName { get { return gameName; } set { SetValue(ref gameName, value); } }

        public BridgeProfileType ProfileType
        {
            get { return profileType; }
            set
            {
                SetValue(ref profileType, value);
                OnPropertyChanged(nameof(ProfileTypeDisplayName));
            }
        }

        public int ConfigurationVersion
        {
            get { return configurationVersion; }
            set { SetValue(ref configurationVersion, value); }
        }

        public BridgeActivationMode ActivationMode
        {
            get { return activationMode; }
            set
            {
                SetValue(ref activationMode, value);
                OnPropertyChanged(nameof(ActivationDisplayName));
            }
        }

        public TouchpadRemappingMode RemappingMode
        {
            get { return remappingMode; }
            set
            {
                SetValue(ref remappingMode, value);
                OnPropertyChanged(nameof(RemappingDisplayName));
            }
        }

        /// <summary>0 = 保持手柄当前板载配置；1..4 = 指定槽位。</summary>
        public int ApexProfileSlot
        {
            get { return apexProfileSlot; }
            set
            {
                SetValue(ref apexProfileSlot, value);
                OnPropertyChanged(nameof(ApexProfileDisplayName));
            }
        }

        [DontSerialize]
        public string ProfileTypeDisplayName
        {
            get { return GetProfileDisplayName(ProfileType); }
        }

        [DontSerialize]
        public string ActivationDisplayName
        {
            get { return GetActivationDisplayName(ActivationMode); }
        }

        [DontSerialize]
        public string RemappingDisplayName
        {
            get { return GetRemappingDisplayName(RemappingMode); }
        }

        [DontSerialize]
        public string ApexProfileDisplayName
        {
            get
            {
                return ApexProfileSlot == 0
                    ? Loc.Get("LOCDsInApex_ApexProfileKeep")
                    : Loc.Format("LOCDsInApex_ApexProfileSlot", ApexProfileSlot);
            }
        }

        internal static string GetProfileDisplayName(BridgeProfileType type)
        {
            switch (type)
            {
                case BridgeProfileType.StandardDualSense:
                    return Loc.Get("LOCDsInApex_ProfileStandard");
                case BridgeProfileType.SpiderMan2:
                    return Loc.Get("LOCDsInApex_ProfileSpiderMan2");
                case BridgeProfileType.MilesMorales:
                    return Loc.Get("LOCDsInApex_ProfileMilesMorales");
                case BridgeProfileType.GhostOfTsushima:
                    return Loc.Get("LOCDsInApex_ProfileGhostOfTsushima");
                case BridgeProfileType.Warframe:
                    return Loc.Get("LOCDsInApex_ProfileWarframe");
                case BridgeProfileType.Disabled:
                    return Loc.Get("LOCDsInApex_ProfileDisabled");
                default:
                    return type.ToString();
            }
        }

        internal static string GetActivationDisplayName(BridgeActivationMode mode)
        {
            switch (mode)
            {
                case BridgeActivationMode.Enabled:
                    return Loc.Get("LOCDsInApex_ActivationEnabled");
                case BridgeActivationMode.Disabled:
                    return Loc.Get("LOCDsInApex_ActivationDisabled");
                default:
                    return Loc.Get("LOCDsInApex_ActivationAuto");
            }
        }

        internal static string GetRemappingDisplayName(TouchpadRemappingMode mode)
        {
            switch (mode)
            {
                case TouchpadRemappingMode.None:
                    return Loc.Get("LOCDsInApex_RemappingNone");
                case TouchpadRemappingMode.SpiderMan2:
                    return Loc.Get("LOCDsInApex_ProfileSpiderMan2");
                case TouchpadRemappingMode.MilesMorales:
                    return Loc.Get("LOCDsInApex_ProfileMilesMorales");
                case TouchpadRemappingMode.GhostOfTsushima:
                    return Loc.Get("LOCDsInApex_ProfileGhostOfTsushima");
                case TouchpadRemappingMode.Warframe:
                    return Loc.Get("LOCDsInApex_ProfileWarframe");
                default:
                    return Loc.Get("LOCDsInApex_RemappingAuto");
            }
        }

        /// <summary>配置档 → <c>--touchpad-profile</c> 命令行取值（与 C++ 解析器逐字对应）。</summary>
        internal static string ToTouchpadProfileArgument(BridgeProfileType type)
        {
            switch (type)
            {
                case BridgeProfileType.SpiderMan2: return "spider-man-2";
                case BridgeProfileType.MilesMorales: return "miles-morales";
                case BridgeProfileType.GhostOfTsushima: return "ghost-of-tsushima";
                case BridgeProfileType.Warframe: return "warframe";
                default: return "none";
            }
        }
    }

    /// <summary>插件配置。</summary>
    public class DsInApexSettings : ObservableObject
    {
        private string deployRootPath = string.Empty;
        private bool enableRumble = true;
        private int hapticThresholdPercent = 12;
        private int initializationTimeoutSeconds = 20;
        private string xinputIndex = string.Empty;
        private bool autoCheckUpdates = true;
        private bool enableAutomaticProfiles = true;
        private DateTime? lastUpdateCheckUtc;
        private List<GameBridgeProfile> profiles = new List<GameBridgeProfile>();

        /// <summary>
        /// 可选：显式指定 Ds in Apex 部署位置（DsInApex.exe 文件或其所在目录）。
        /// 留空时走自动探测（运行中进程 → 插件目录上溯 → 常见位置）。
        /// 便携版部署在非常规位置时靠它兜底。
        /// </summary>
        public string DeployRootPath
        {
            get { return deployRootPath; }
            set { SetValue(ref deployRootPath, value); }
        }

        public bool EnableRumble
        {
            get { return enableRumble; }
            set { SetValue(ref enableRumble, value); }
        }

        public int HapticThresholdPercent
        {
            get { return hapticThresholdPercent; }
            set { SetValue(ref hapticThresholdPercent, value); }
        }

        public int InitializationTimeoutSeconds
        {
            get { return initializationTimeoutSeconds; }
            set { SetValue(ref initializationTimeoutSeconds, value); }
        }

        /// <summary>上游遗留字段，仅为迁移旧配置保留，不再使用。</summary>
        public string XInputIndex
        {
            get { return xinputIndex; }
            set { SetValue(ref xinputIndex, value); }
        }

        public bool AutoCheckUpdates
        {
            get { return autoCheckUpdates; }
            set { SetValue(ref autoCheckUpdates, value); }
        }

        public bool EnableAutomaticProfiles
        {
            get { return enableAutomaticProfiles; }
            set { SetValue(ref enableAutomaticProfiles, value); }
        }

        public DateTime? LastUpdateCheckUtc
        {
            get { return lastUpdateCheckUtc; }
            set { SetValue(ref lastUpdateCheckUtc, value); }
        }

        public List<GameBridgeProfile> Profiles
        {
            get { return profiles; }
            set { SetValue(ref profiles, value ?? new List<GameBridgeProfile>()); }
        }

        internal GameBridgeProfile FindProfile(Guid gameId)
        {
            return Profiles.FirstOrDefault(profile => profile.GameId == gameId);
        }

        internal GameBridgeProfile ResolveProfile(Game game, out bool automatic, out string reason)
        {
            GameBridgeProfile configured = FindProfile(game.Id);
            BridgeProfileType detectedProfile;
            bool detected = AutomaticProfileDetector.TryDetect(game, out detectedProfile, out reason);
            BridgeActivationMode activation = configured == null
                ? BridgeActivationMode.Automatic
                : configured.ActivationMode;
            automatic = activation == BridgeActivationMode.Automatic;

            bool enabled = activation == BridgeActivationMode.Enabled ||
                (activation == BridgeActivationMode.Automatic &&
                 EnableAutomaticProfiles && detected);
            if (!enabled || activation == BridgeActivationMode.Disabled)
            {
                return null;
            }

            TouchpadRemappingMode remapping = configured == null
                ? TouchpadRemappingMode.Automatic
                : configured.RemappingMode;
            BridgeProfileType effectiveProfile = remapping == TouchpadRemappingMode.Automatic
                ? (detected ? detectedProfile : BridgeProfileType.StandardDualSense)
                : ToBridgeProfileType(remapping);
            if (!detected && activation == BridgeActivationMode.Enabled)
            {
                reason = "manual activation";
            }

            return new GameBridgeProfile
            {
                GameId = game.Id,
                GameName = game.Name,
                ConfigurationVersion = 2,
                ActivationMode = activation,
                RemappingMode = remapping,
                ApexProfileSlot = configured == null ? 0 : configured.ApexProfileSlot,
                ProfileType = effectiveProfile
            };
        }

        /// <summary>把 0.6.1 时代的旧配置（只有 ProfileType）迁移到 v2 结构。</summary>
        internal void MigrateLegacyProfiles()
        {
            foreach (GameBridgeProfile profile in Profiles)
            {
                if (profile.ConfigurationVersion >= 2)
                {
                    profile.ApexProfileSlot = Math.Max(0, Math.Min(4, profile.ApexProfileSlot));
                    continue;
                }

                profile.ActivationMode = profile.ProfileType == BridgeProfileType.Disabled
                    ? BridgeActivationMode.Disabled
                    : BridgeActivationMode.Enabled;
                profile.RemappingMode = FromLegacyProfileType(profile.ProfileType);
                profile.ApexProfileSlot = 0;
                profile.ConfigurationVersion = 2;
            }
        }

        internal void SetActivation(Game game, BridgeActivationMode mode)
        {
            GameBridgeProfile profile = GetOrCreateProfile(game);
            profile.ActivationMode = mode;
            PruneEmptyProfile(profile);
            OnPropertyChanged(nameof(Profiles));
        }

        internal void SetRemapping(Game game, TouchpadRemappingMode mode)
        {
            GameBridgeProfile profile = GetOrCreateProfile(game);
            profile.RemappingMode = mode;
            PruneEmptyProfile(profile);
            OnPropertyChanged(nameof(Profiles));
        }

        internal void SetApexProfile(Game game, int slot)
        {
            GameBridgeProfile profile = GetOrCreateProfile(game);
            profile.ApexProfileSlot = Math.Max(0, Math.Min(4, slot));
            PruneEmptyProfile(profile);
            OnPropertyChanged(nameof(Profiles));
        }

        internal void RemoveProfile(Guid gameId)
        {
            Profiles.RemoveAll(profile => profile.GameId == gameId);
            OnPropertyChanged(nameof(Profiles));
        }

        private GameBridgeProfile GetOrCreateProfile(Game game)
        {
            GameBridgeProfile profile = FindProfile(game.Id);
            if (profile == null)
            {
                profile = new GameBridgeProfile
                {
                    GameId = game.Id,
                    ConfigurationVersion = 2,
                    ActivationMode = BridgeActivationMode.Automatic,
                    RemappingMode = TouchpadRemappingMode.Automatic
                };
                Profiles.Add(profile);
            }
            profile.GameName = game.Name;
            return profile;
        }

        private void PruneEmptyProfile(GameBridgeProfile profile)
        {
            if (profile.ActivationMode == BridgeActivationMode.Automatic &&
                profile.RemappingMode == TouchpadRemappingMode.Automatic &&
                profile.ApexProfileSlot == 0)
            {
                Profiles.Remove(profile);
            }
        }

        private static TouchpadRemappingMode FromLegacyProfileType(BridgeProfileType type)
        {
            switch (type)
            {
                case BridgeProfileType.SpiderMan2:
                    return TouchpadRemappingMode.SpiderMan2;
                case BridgeProfileType.MilesMorales:
                    return TouchpadRemappingMode.MilesMorales;
                case BridgeProfileType.GhostOfTsushima:
                    return TouchpadRemappingMode.GhostOfTsushima;
                case BridgeProfileType.Warframe:
                    return TouchpadRemappingMode.Warframe;
                default:
                    // 0.6.1 的 Standard 本来就靠自动识别特殊手势
                    return TouchpadRemappingMode.Automatic;
            }
        }

        private static BridgeProfileType ToBridgeProfileType(TouchpadRemappingMode mode)
        {
            switch (mode)
            {
                case TouchpadRemappingMode.SpiderMan2:
                    return BridgeProfileType.SpiderMan2;
                case TouchpadRemappingMode.MilesMorales:
                    return BridgeProfileType.MilesMorales;
                case TouchpadRemappingMode.GhostOfTsushima:
                    return BridgeProfileType.GhostOfTsushima;
                case TouchpadRemappingMode.Warframe:
                    return BridgeProfileType.Warframe;
                default:
                    return BridgeProfileType.StandardDualSense;
            }
        }
    }

    /// <summary>设置页 ViewModel（同时实现 Playnite 的 <see cref="ISettings"/> 契约）。</summary>
    public class DsInApexSettingsViewModel : ObservableObject, ISettings
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly DsInApexPlugin plugin;
        private DsInApexSettings editingClone;
        private DsInApexSettings settings;

        private bool isCheckingForUpdate;
        private string updateCheckStatus = string.Empty;
        private bool isUpdateAvailable;
        private string availableUpdateVersion = string.Empty;
        private string availableReleaseUrl = string.Empty;

        public DsInApexSettings Settings
        {
            get { return settings; }
            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        public string CurrentVersionDisplay
        {
            get
            {
                string version = plugin.GetDeployedVersion();
                return string.IsNullOrWhiteSpace(version)
                    ? Loc.Get("LOCDsInApex_StatusVersionUnknown")
                    : "v" + version;
            }
        }

        public string ConfiguredDeployPath
        {
            get { return Settings == null ? string.Empty : Settings.DeployRootPath; }
            set
            {
                if (Settings == null ||
                    string.Equals(Settings.DeployRootPath, value, StringComparison.Ordinal))
                {
                    return;
                }

                Settings.DeployRootPath = value ?? string.Empty;
                RefreshInstallationStatus();
            }
        }

        public string InstalledExecutablePath
        {
            get
            {
                string path = plugin.ResolveEnginePath(Settings);
                return string.IsNullOrWhiteSpace(path) ? Loc.Get("LOCDsInApex_StatusNotInstalled") : path;
            }
        }

        public string InstallationStatus
        {
            get
            {
                if (InstallLocator.IsDeployedApp(ConfiguredDeployPath) ||
                    InstallLocator.IsEngine(ConfiguredDeployPath))
                {
                    return Loc.Get("LOCDsInApex_StatusConfiguredManually");
                }

                return string.IsNullOrWhiteSpace(plugin.ResolveDeployRoot(Settings))
                    ? Loc.Get("LOCDsInApex_StatusInstallationNotFound")
                    : Loc.Get("LOCDsInApex_StatusDetectedAutomatically");
            }
        }

        public bool IsCheckingForUpdate
        {
            get { return isCheckingForUpdate; }
            set
            {
                SetValue(ref isCheckingForUpdate, value);
                OnPropertyChanged(nameof(IsNotCheckingForUpdate));
            }
        }

        /// <summary>供「检查更新」按钮的 IsEnabled 直接绑定（WPF 没有内置反布尔转换器）。</summary>
        public bool IsNotCheckingForUpdate
        {
            get { return !isCheckingForUpdate; }
        }

        public string UpdateCheckStatus
        {
            get { return updateCheckStatus; }
            set { SetValue(ref updateCheckStatus, value); }
        }

        public bool IsUpdateAvailable
        {
            get { return isUpdateAvailable; }
            set { SetValue(ref isUpdateAvailable, value); }
        }

        public string AvailableUpdateVersion
        {
            get { return availableUpdateVersion; }
            set { SetValue(ref availableUpdateVersion, value); }
        }

        public RelayCommand CheckForUpdatesCommand { get; private set; }

        public RelayCommand OpenReleasePageCommand { get; private set; }

        public RelayCommand BrowseForExecutableCommand { get; private set; }

        public RelayCommand UseAutomaticInstallationCommand { get; private set; }

        public DsInApexSettingsViewModel(DsInApexPlugin plugin)
        {
            this.plugin = plugin;
            Settings = plugin.LoadPluginSettings<DsInApexSettings>() ?? new DsInApexSettings();
            Settings.MigrateLegacyProfiles();

            CheckForUpdatesCommand = new RelayCommand(async () => await CheckForUpdatesAsync());
            OpenReleasePageCommand = new RelayCommand(OpenReleasePage);
            BrowseForExecutableCommand = new RelayCommand(BrowseForExecutable);
            UseAutomaticInstallationCommand = new RelayCommand(() => ConfiguredDeployPath = string.Empty);
        }

        private void OpenReleasePage()
        {
            UpdateManager.OpenReleasePage(availableReleaseUrl);
        }

        private void BrowseForExecutable()
        {
            string selectedPath = plugin.PlayniteApi.Dialogs.SelectFile(
                Loc.Get("LOCDsInApex_BrowseFilter"));
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            if (!InstallLocator.IsDeployedApp(selectedPath))
            {
                plugin.PlayniteApi.Dialogs.ShowErrorMessage(
                    Loc.Get("LOCDsInApex_ErrSelectWrongExe"),
                    Loc.Get("LOCDsInApex_DlgTitle"));
                return;
            }

            ConfiguredDeployPath = Path.GetFullPath(selectedPath);
        }

        private void RefreshInstallationStatus()
        {
            OnPropertyChanged(nameof(ConfiguredDeployPath));
            OnPropertyChanged(nameof(InstalledExecutablePath));
            OnPropertyChanged(nameof(InstallationStatus));
            OnPropertyChanged(nameof(CurrentVersionDisplay));
        }

        internal async Task CheckForUpdatesAsync()
        {
            if (IsCheckingForUpdate)
            {
                return;
            }

            IsCheckingForUpdate = true;
            UpdateCheckStatus = Loc.Get("LOCDsInApex_StatusChecking");

            try
            {
                UpdateCheckResult result = await UpdateManager
                    .CheckForUpdateAsync(plugin.GetDeployedVersion())
                    .ConfigureAwait(true);

                Settings.LastUpdateCheckUtc = DateTime.UtcNow;

                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    UpdateCheckStatus = result.ErrorMessage;
                    IsUpdateAvailable = false;
                }
                else if (result.IsUpdateAvailable)
                {
                    IsUpdateAvailable = true;
                    AvailableUpdateVersion = result.TagName;
                    availableReleaseUrl = result.ReleaseUrl;
                    UpdateCheckStatus = Loc.Format("LOCDsInApex_StatusUpdateAvailable", result.TagName);
                }
                else
                {
                    IsUpdateAvailable = false;
                    UpdateCheckStatus = Loc.Format("LOCDsInApex_StatusLatest", CurrentVersionDisplay);
                }
            }
            catch (Exception ex)
            {
                UpdateCheckStatus = Loc.Format("LOCDsInApex_StatusError", ex.Message);
                Logger.Error(ex, "手动检查更新失败。");
            }
            finally
            {
                IsCheckingForUpdate = false;
            }
        }

        public void BeginEdit()
        {
            editingClone = Serialization.GetClone(Settings);
            RefreshInstallationStatus();
            OnPropertyChanged(nameof(CurrentVersionDisplay));
        }

        public void CancelEdit()
        {
            Settings = editingClone;
            RefreshInstallationStatus();
        }

        public void EndEdit()
        {
            SaveNow();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            if (string.IsNullOrWhiteSpace(plugin.ResolveEnginePath(Settings)))
            {
                errors.Add(Loc.Get("LOCDsInApex_VerifyDeployRootNotFound"));
            }
            if (Settings.HapticThresholdPercent < 0 || Settings.HapticThresholdPercent > 95)
            {
                errors.Add(Loc.Get("LOCDsInApex_VerifyHapticRange"));
            }
            if (Settings.InitializationTimeoutSeconds < 5 || Settings.InitializationTimeoutSeconds > 60)
            {
                errors.Add(Loc.Get("LOCDsInApex_VerifyTimeoutRange"));
            }

            return errors.Count == 0;
        }

        internal void SaveNow()
        {
            plugin.SaveSettings(Settings);
        }
    }
}
