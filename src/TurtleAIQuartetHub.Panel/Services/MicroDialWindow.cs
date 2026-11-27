using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TurtleAIQuartetHub.Panel.Models;

namespace TurtleAIQuartetHub.Panel.Services;

// 極小表示（Micro モード）用の円形オービット・ダイヤル（案 B）。
//
// 設計ポイント:
//   - MainWindow（AllowsTransparency=False）はタスクバー連携と標準／縮小表示の安定性を維持したまま
//     DWMWA_CLOAK で隠し、極小表示の間だけこの正式な透過ウィンドウ（AllowsTransparency=True）を表示する。
//   - 背景は完全透明（Brushes.Transparent）のため、非展開時は直径 116 DIP の真円コア以外がすべて
//     WPF ヒットテストを素通りし、背後アプリ（VS Code 等）をそのまま操作できる。
//   - 普段は半透明ゴースト（既定 0.30、AI 完了時のみ 0.86 へ自動ブースト。フォーカス中・AI 実行中も非ホバー時は薄く表示）
//     で中央に静かに浮かび、マウス進入で 1.0 へ実体化＋外周リングへモード切替・控え一覧・前後・補助 AI ボタンが開花する。
//   - 扇ホバー／ホイール回転中は遅延ゼロ（0ms）のライブ情報ピル Popup にフルタイトル・アプリ種別・AI 状態を表示する。
internal sealed class MicroDialWindow : Window
{
    public const double StageSizeDip = 228.0;
    public const double CoreDiameterDip = 116.0;
    public const double CoreRadiusDip = CoreDiameterDip / 2.0;
    public const double StageCenterDip = StageSizeDip / 2.0;

    // 本体円の背面に重ねる同心ソフト影（外側 +8 DIP／内側 +4 DIP の均一な縁取り）。
    private const double CoreShadowOuterDiameterDip = CoreDiameterDip + 16.0;
    private const double CoreShadowInnerDiameterDip = CoreDiameterDip + 8.0;

    // 本体円の外枠線（_coreOuterRing）の太さ。
    private const double CoreRingStrokeThicknessDip = 1.4;

    // 外周の実行中 LED 周回（AiCardOrbitGlow）の要素の一辺。AiCardOrbitGlow は要素の 0.5 DIP 内側を周回するため、
    // その分を差し引いて、周回の中心線が本体円の外枠線の中心線（半径 58 − 0.7 = 57.3 DIP）にぴったり重なる寸法にする。
    private const double CoreWorkingOrbitSizeDip =
        CoreDiameterDip - (2.0 * ((CoreRingStrokeThicknessDip / 2.0) - AiCardOrbitGlow.BorderCenterInset));

    // 外周 LED 周回の 1 周の秒数の倍率。画面中央で目につきすぎないよう、周長から決まる標準（約 2.73 秒）より
    // ゆっくり（約 4.1 秒）回す。光の太さ・尾の長さ・加速カーブ・60fps の滑らかさは標準・縮小表示と同じまま。
    private const double CoreWorkingOrbitCycleDurationScale = 1.5;

    private const double SatelliteOrbitRadiusDip = 81.0;
    private const double SectorOuterRadiusDip = 51.0;
    private const double SectorInnerRadiusDip = 18.5;
    private const double CenterButtonDiameterDip = 30.0;
    private const double DragStartThresholdDip = 7.0;
    private const double IdleAiAlertOpacity = 0.86;

    // タスクバーのサムネイルで円の周囲に取る余白（DIP）。円が枠いっぱいに詰まらず、かつ十分大きく見える幅にする。
    private const double TaskbarThumbnailPaddingDip = 9.0;

    private static readonly TimeSpan ExpandAnimDuration = TimeSpan.FromMilliseconds(190);
    private static readonly TimeSpan CollapseAnimDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan LeaveGraceDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan AiWorkingFireflyDuration = TimeSpan.FromSeconds(4.8);
    private static readonly TimeSpan AiDoneCycleDuration = TimeSpan.FromSeconds(3.0);

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const uint VkEscape = 0x1B;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcRButtonDown = 0x00A4;
    private static readonly IntPtr HwndTopmost = new(-1);

    private static readonly Brush CoreBackgroundBrush = CreateFrozenBrush(Color.FromRgb(0x0F, 0x14, 0x12));
    // タスクバーのサムネイルの不透明背景。標準・縮小表示のサムネイルと揃うよう、本体パネル（MainWindow.Background）と同じ #101211。
    private static readonly Brush TaskbarThumbnailBackgroundBrush = CreateFrozenBrush(Color.FromRgb(0x10, 0x12, 0x11));
    private static readonly Brush CoreIdleBorderBrush = CreateFrozenBrush(Color.FromRgb(0x2F, 0x4A, 0x3C));
    private static readonly Brush CoreActiveBorderBrush = CreateFrozenBrush(Color.FromRgb(0x45, 0xD4, 0x83));
    private static readonly Brush SectorReadyFillBrush = CreateFrozenBrush(Color.FromRgb(0x17, 0x26, 0x1E));
    private static readonly Brush SectorReadyStrokeBrush = CreateFrozenBrush(Color.FromRgb(0x34, 0x5C, 0x46));
    private static readonly Brush SectorMissingFillBrush = CreateFrozenBrush(Color.FromRgb(0x11, 0x15, 0x13));
    private static readonly Brush SectorMissingStrokeBrush = CreateFrozenBrush(Color.FromRgb(0x27, 0x31, 0x2C));
    private static readonly Brush SectorLaunchingFillBrush = CreateFrozenBrush(Color.FromRgb(0x24, 0x1F, 0x15));
    private static readonly Brush SectorLaunchingStrokeBrush = CreateFrozenBrush(Color.FromRgb(0xA2, 0x7E, 0x39));
    private static readonly Brush SectorHiddenFillBrush = CreateFrozenBrush(Color.FromRgb(0x1E, 0x1C, 0x14));
    private static readonly Brush SectorHiddenStrokeBrush = CreateFrozenBrush(Color.FromRgb(0x73, 0x67, 0x35));
    private static readonly Brush SectorHoverFillBrush = CreateFrozenBrush(Color.FromRgb(0x1E, 0x31, 0x27));
    private static readonly Brush SectorMissingHoverFillBrush = CreateFrozenBrush(Color.FromRgb(0x18, 0x20, 0x1D));
    private static readonly Brush SectorFocusedFillBrush = CreateFrozenBrush(Color.FromRgb(0x15, 0x3C, 0x28));
    private static readonly Brush SectorAssignFillBrush = CreateFrozenBrush(Color.FromRgb(0x18, 0x3E, 0x2B));
    private static readonly Brush SectorBaseStrokeBrush = CreateFrozenBrush(Color.FromRgb(0x2E, 0x3E, 0x36));
    // ホバー枠は 1 面フォーカス中の太い鮮やか緑枠（2.0px, #45D483）と明確に見分けられるよう、控えめな細枠トーンにする。
    private static readonly Brush SectorHoverStrokeBrush = CreateFrozenBrush(Color.FromArgb(0xC8, 0x72, 0xB8, 0x92));
    private static readonly Brush SectorAssignStrokeBrush = CreateFrozenBrush(Color.FromRgb(0x45, 0xD4, 0x83));
    private static readonly Brush AiDoneBaseFillBrush = CreateFrozenBrush(Color.FromRgb(0x13, 0x42, 0x6F));
    private static readonly Brush AiDoneWashFillBrush = CreateFrozenBrush(Color.FromRgb(0x25, 0x77, 0xCB));
    private static readonly Brush AiDoneStrokeBrush = CreateFrozenBrush(Color.FromRgb(0x68, 0xC1, 0xFF));
    private static readonly Brush TextPrimaryBrush = CreateFrozenBrush(Color.FromRgb(0xEE, 0xF4, 0xEF));
    private static readonly Brush TextReadySubBrush = CreateFrozenBrush(Color.FromRgb(0xDF, 0xEC, 0xE4));
    private static readonly Brush TextFocusedKeyBrush = CreateFrozenBrush(Color.FromRgb(0x78, 0xF4, 0xAD));
    private static readonly Brush TextMissingKeyBrush = CreateFrozenBrush(Color.FromRgb(0x6F, 0x7B, 0x74));
    private static readonly Brush TextMissingSubBrush = CreateFrozenBrush(Color.FromRgb(0x66, 0x72, 0x6B));
    private static readonly Brush TextLaunchingKeyBrush = CreateFrozenBrush(Color.FromRgb(0xF2, 0xCA, 0x57));
    private static readonly Brush TextLaunchingSubBrush = CreateFrozenBrush(Color.FromRgb(0xE8, 0xD2, 0x8A));
    private static readonly Brush TextHiddenKeyBrush = CreateFrozenBrush(Color.FromRgb(0xD4, 0xC4, 0x7A));
    private static readonly Brush TextHiddenSubBrush = CreateFrozenBrush(Color.FromRgb(0xC5, 0xBC, 0x94));
    private static readonly Brush TextMutedBrush = CreateFrozenBrush(Color.FromRgb(0xA6, 0xBC, 0xB0));
    private static readonly Brush TextAiDoneBrush = CreateFrozenBrush(Color.FromRgb(0xF0, 0xF8, 0xFF));
    private static readonly Brush TextAiDoneSubBrush = CreateFrozenBrush(Color.FromRgb(0xBE, 0xE3, 0xFF));
    private static readonly Brush AccentGreenBrush = CreateFrozenBrush(Color.FromRgb(0x45, 0xD4, 0x83));
    private static readonly Brush AccentBlueBrush = CreateFrozenBrush(Color.FromRgb(0x74, 0xCB, 0xFF));
    private static readonly Brush StatusReadyDotBrush = CreateFrozenBrush(Color.FromRgb(0x45, 0xD4, 0x83));
    private static readonly Brush StatusLaunchingDotBrush = CreateFrozenBrush(Color.FromRgb(0xF2, 0xCA, 0x57));
    private static readonly Brush StatusMissingDotBrush = CreateFrozenBrush(Color.FromRgb(0xD8, 0x77, 0x6F));
    private static readonly Brush StatusHiddenDotBrush = CreateFrozenBrush(Color.FromRgb(0xD4, 0xC4, 0x7A));
    private static readonly Brush StatusAiDoneDotBrush = CreateFrozenBrush(Color.FromRgb(0x74, 0xCB, 0xFF));
    private static readonly Brush PillReadyBgBrush = CreateFrozenBrush(Color.FromRgb(0x14, 0x2B, 0x1E));
    private static readonly Brush PillReadyBorderBrush = CreateFrozenBrush(Color.FromRgb(0x2E, 0x7A, 0x4D));
    private static readonly Brush PillReadyFgBrush = CreateFrozenBrush(Color.FromRgb(0x52, 0xDF, 0x8E));
    private static readonly Brush PillFocusedBgBrush = CreateFrozenBrush(Color.FromRgb(0x15, 0x38, 0x26));
    private static readonly Brush PillFocusedFgBrush = CreateFrozenBrush(Color.FromRgb(0x7C, 0xF2, 0xAE));
    private static readonly Brush PillLaunchingBgBrush = CreateFrozenBrush(Color.FromRgb(0x2C, 0x24, 0x14));
    private static readonly Brush PillLaunchingBorderBrush = CreateFrozenBrush(Color.FromRgb(0xA2, 0x7E, 0x39));
    private static readonly Brush PillLaunchingFgBrush = CreateFrozenBrush(Color.FromRgb(0xF2, 0xCA, 0x57));
    private static readonly Brush PillMissingBgBrush = CreateFrozenBrush(Color.FromRgb(0x2A, 0x1A, 0x18));
    private static readonly Brush PillMissingBorderBrush = CreateFrozenBrush(Color.FromRgb(0x6A, 0x38, 0x32));
    private static readonly Brush PillMissingFgBrush = CreateFrozenBrush(Color.FromRgb(0xF2, 0xB7, 0xAF));
    private static readonly Brush PillHiddenBgBrush = CreateFrozenBrush(Color.FromRgb(0x28, 0x25, 0x1A));
    private static readonly Brush PillHiddenBorderBrush = CreateFrozenBrush(Color.FromRgb(0x8B, 0x7D, 0x3E));
    private static readonly Brush PillHiddenFgBrush = CreateFrozenBrush(Color.FromRgb(0xD4, 0xC4, 0x7A));
    private static readonly Brush PillAiDoneBgBrush = CreateFrozenBrush(Color.FromRgb(0x10, 0x2D, 0x4A));
    private static readonly Brush PillAiDoneBorderBrush = CreateFrozenBrush(Color.FromRgb(0x3E, 0xA3, 0xEC));
    private static readonly Brush PillAiDoneFgBrush = CreateFrozenBrush(Color.FromRgb(0x9C, 0xD9, 0xFF));
    private static readonly Brush KeyBadgeMissingBgBrush = CreateFrozenBrush(Color.FromRgb(0x1B, 0x21, 0x1E));
    private static readonly Brush KeyBadgeMissingBorderBrush = CreateFrozenBrush(Color.FromRgb(0x4A, 0x55, 0x50));
    private static readonly Brush CenterNormalBgBrush = CreateFrozenBrush(Color.FromRgb(0x10, 0x26, 0x1B));
    private static readonly Brush CenterNormalFgBrush = CreateFrozenBrush(Color.FromRgb(0x7A, 0xE9, 0xAB));
    private static readonly Brush CenterRestoreBgBrush = CreateFrozenBrush(Color.FromRgb(0xE6, 0xBF, 0x4E));
    private static readonly Brush CenterRestoreBorderBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0xF0, 0xB3));
    private static readonly Brush CenterRestoreFgBrush = CreateFrozenBrush(Color.FromRgb(0x11, 0x16, 0x13));
    private static readonly Brush SatBgBrush = CreateFrozenBrush(Color.FromArgb(0xF0, 0x12, 0x18, 0x15));
    private static readonly Brush SatHoverBgBrush = CreateFrozenBrush(Color.FromRgb(0x1B, 0x3B, 0x2A));
    private static readonly Brush SatActiveBgBrush = CreateFrozenBrush(Color.FromRgb(0x20, 0x47, 0x33));
    private static readonly Brush SatBorderBrush = CreateFrozenBrush(Color.FromArgb(0x88, 0x45, 0xD4, 0x83));
    private static readonly Brush SatCloseHoverBorderBrush = CreateFrozenBrush(Color.FromRgb(0xDC, 0x5B, 0x52));
    private static readonly Brush VsCodeDotBrush = CreateFrozenBrush(Color.FromRgb(0x37, 0xA4, 0xE8));
    private static readonly Brush AntigravityDotBrush = CreateFrozenBrush(Color.FromRgb(0xA7, 0x8B, 0xFA));
    private static readonly Brush CliDotBrush = CreateFrozenBrush(Color.FromRgb(0x45, 0xD4, 0x83));
    private static readonly Brush MenuBgBrush = CreateFrozenBrush(Color.FromRgb(0x1D, 0x26, 0x22));
    private static readonly Brush MenuBorderBrush = CreateFrozenBrush(Color.FromRgb(0x3E, 0x51, 0x48));
    private static readonly Brush MenuHoverBgBrush = CreateFrozenBrush(Color.FromRgb(0x25, 0x30, 0x2A));
    private static readonly Brush MenuSeparatorBrush = CreateFrozenBrush(Color.FromRgb(0x2E, 0x38, 0x32));

    private readonly IReadOnlyList<WindowSlot> _slots;
    private readonly Func<IReadOnlyList<LauncherApplication>> _getAuxApplications;
    private readonly Action<WindowSlot> _onSlotActivated;
    private readonly Action<StoredPanelSlot, WindowSlot> _onStoredPanelDropped;
    private readonly Action<WindowSlot, WindowSlot> _onSlotSwapped;
    private readonly Action _onToggleVisibility;
    private readonly Action _onToggleStoredOverview;
    private readonly Action _onPinTop;
    private readonly Action _onSendBack;
    private readonly Action<LauncherApplication> _onLaunchAuxApp;
    private readonly Action _onSwitchStandard;
    private readonly Action _onSwitchCompact;
    private readonly Action _onMinimize;
    private readonly Action _onRequestClose;
    private readonly Action<bool, int> _onGhostOpacityChanged;
    private readonly Action _onRecenterRequested;
    private readonly Action<Point> _onCoreCenterMoved;
    private readonly Action? _onOpenDownloadsFolder;
    private readonly Action? _onOpenPullRequestInbox;
    private readonly Style _contextMenuStyle;
    private readonly Func<WindowSlot?, bool, int, Action<bool, int>, Action, ContextMenu>? _buildContextMenu;

    private readonly Grid _stageRoot;
    private readonly Grid _dialRoot;
    private readonly Canvas _satelliteCanvas;
    private readonly ScaleTransform _satelliteScale = new(0.86, 0.86);
    private readonly Grid _coreHost;
    private readonly Ellipse _coreShadowOuter;
    private readonly Ellipse _coreShadowInner;
    private readonly Ellipse _coreOuterRing;
    private readonly AiCardOrbitGlow _coreWorkingOrbitGlow;
    private readonly Button _centerVisibilityButton;
    private readonly Border _centerVisibilityBorder;
    private readonly TextBlock _centerVisibilityText;
    private readonly Button _storedSatelliteButton;
    private readonly Border _storedSatelliteBorder;
    private readonly Button _auxAiSatelliteButton;
    private readonly Border _auxAiSatelliteBorder;
    private readonly Button _pinTopSatelliteButton;
    private readonly Button _sendBackSatelliteButton;
    private readonly Button _standardSatelliteButton;
    private readonly Button _compactSatelliteButton;
    private readonly Button _downloadsSatelliteButton;
    private readonly Button _pullRequestSatelliteButton;
    private readonly Ellipse _pullRequestBadgeDot;
    private readonly SectorVisual[] _sectors = new SectorVisual[4];

    private readonly Popup _infoPillPopup;
    private readonly Border _infoPillBorder;
    private readonly Border _infoPillKeyBadge;
    private readonly TextBlock _infoPillKeyText;
    private readonly TextBlock _infoPillTitleText;
    private readonly Ellipse _infoPillAppDot;
    private readonly TextBlock _infoPillAppText;
    private readonly Border _infoPillStatusBadge;
    private readonly TextBlock _infoPillStatusText;
    private readonly TextBlock _infoPillAssignHint;

    private readonly DispatcherTimer _leaveGraceTimer;
    private bool _ghostOpacityEnabled;
    private int _ghostOpacityPercent;
    private bool _isHovered;
    private bool _isExpanded;
    private bool _isStoredOverviewOpen;
    private bool _isContextMenuOpen;
    private ContextMenu? _activeContextMenu;
    private IntPtr _outsideClickMouseHookHandle;
    private IntPtr _outsideEscapeKeyboardHookHandle;
    // WH_MOUSE_LL / WH_KEYBOARD_LL コールバックデリゲートを GC 回収から保護するためインスタンスフィールドで保持する。
    private readonly LowLevelHookProc _outsideClickMouseHookProc;
    private readonly LowLevelHookProc _outsideEscapeKeyboardHookProc;
    private readonly Popup _auxApplicationsPopup;
    private readonly StackPanel _auxApplicationsListPanel;
    private bool _areWindowsHidden;
    private int? _hoveredSectorIndex;
    private int _selectedSectorIndex;
    private bool _wheelSelectActive;
    private StoredPanelSlot? _pendingStoredPanel;

    private bool _isPointerDown;
    private bool _isDraggingWindow;
    private bool _pointerDownOnCenterButton;
    private bool _suppressNextCenterButtonClick;
    private int? _pointerDownSectorIndex;
    private Point _dragStartScreenPx;
    private double _dragStartWindowLeft;
    private double _dragStartWindowTop;
    private bool _closed;

    public MicroDialWindow(
        IReadOnlyList<WindowSlot> slots,
        bool ghostOpacityEnabled,
        Func<IReadOnlyList<LauncherApplication>> getAuxApplications,
        Action<WindowSlot> onSlotActivated,
        Action<StoredPanelSlot, WindowSlot> onStoredPanelDropped,
        Action<WindowSlot, WindowSlot> onSlotSwapped,
        Action onToggleVisibility,
        Action onToggleStoredOverview,
        Action onPinTop,
        Action onSendBack,
        Action<LauncherApplication> onLaunchAuxApp,
        Action onSwitchStandard,
        Action onSwitchCompact,
        Action onMinimize,
        Action onRequestClose,
        Action<bool, int> onGhostOpacityChanged,
        Action onRecenterRequested,
        Action<Point> onCoreCenterMoved,
        Style? contextMenuStyle = null,
        Func<WindowSlot?, bool, int, Action<bool, int>, Action, ContextMenu>? buildContextMenu = null,
        Action? onOpenDownloadsFolder = null,
        Action? onOpenPullRequestInbox = null,
        Geometry? pullRequestIconGeometry = null,
        int ghostOpacityPercent = AppConfig.DefaultMicroGhostOpacityPercent)
    {
        _slots = slots;
        _ghostOpacityEnabled = ghostOpacityEnabled;
        _ghostOpacityPercent = AppConfig.NormalizeMicroGhostOpacityPercent(ghostOpacityPercent);
        _getAuxApplications = getAuxApplications;
        _onSlotActivated = onSlotActivated;
        _onStoredPanelDropped = onStoredPanelDropped;
        _onSlotSwapped = onSlotSwapped;
        _onToggleVisibility = onToggleVisibility;
        _onToggleStoredOverview = onToggleStoredOverview;
        _onPinTop = onPinTop;
        _onSendBack = onSendBack;
        _onLaunchAuxApp = onLaunchAuxApp;
        _onSwitchStandard = onSwitchStandard;
        _onSwitchCompact = onSwitchCompact;
        _onMinimize = onMinimize;
        _onRequestClose = onRequestClose;
        _onGhostOpacityChanged = onGhostOpacityChanged;
        _onRecenterRequested = onRecenterRequested;
        _onCoreCenterMoved = onCoreCenterMoved;
        _onOpenDownloadsFolder = onOpenDownloadsFolder;
        _onOpenPullRequestInbox = onOpenPullRequestInbox;
        _contextMenuStyle = contextMenuStyle ?? CreateFallbackDarkContextMenuStyle();
        _buildContextMenu = buildContextMenu;
        _outsideClickMouseHookProc = OutsideClickMouseHookCallback;
        _outsideEscapeKeyboardHookProc = OutsideEscapeKeyboardHookCallback;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = StageSizeDip;
        Height = StageSizeDip;
        UseLayoutRounding = false;
        SnapsToDevicePixels = false;

        _leaveGraceTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = LeaveGraceDuration
        };
        _leaveGraceTimer.Tick += (_, _) =>
        {
            _leaveGraceTimer.Stop();
            if (!_isHovered && !ShouldRemainExpanded())
            {
                SetExpandedState(false);
            }
        };

        // 1. 外周サテライト群（ホバー時に滑らかに開花。非展開時は完全クリックスルー）
        _satelliteCanvas = new Canvas
        {
            Width = StageSizeDip,
            Height = StageSizeDip,
            Opacity = 0.0,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _satelliteScale
        };

        // 上弧 4 ボタン（標準＝フル 4 タイル、縮小＝極細横長バー、最小化、閉じる）
        (_standardSatelliteButton, _) = AddCircularSatelliteWithContent(
            angleDeg: -124,
            content: MainWindow.CreateStandardModeIcon(),
            toolTip: "標準表示へ戻す",
            isClose: false,
            () => _onSwitchStandard());
        (_compactSatelliteButton, _) = AddCircularSatelliteWithContent(
            angleDeg: -102,
            content: MainWindow.CreateCompactModeIcon(),
            toolTip: "縮小表示へ切り替え",
            isClose: false,
            () => _onSwitchCompact());
        AddCircularSatellite(angleDeg: -78, glyph: "\uE921", isMdl2: true, toolTip: "最小化", isClose: false, () => _onMinimize());
        AddCircularSatellite(angleDeg: -56, glyph: "\uE8BB", isMdl2: true, toolTip: "パネルを閉じる", isClose: true, () => _onRequestClose());

        // 左弧 2 ボタン（上＝ダウンロードフォルダ、下＝GitHub Pull Request 受信箱・未読赤丸バッジ連動）
        (_downloadsSatelliteButton, _) = AddCircularSatellite(
            angleDeg: -166.5,
            glyph: "\uE896",
            isMdl2: true,
            toolTip: "ダウンロードフォルダを開く",
            isClose: false,
            () => _onOpenDownloadsFolder?.Invoke());

        var prGeometry = pullRequestIconGeometry ?? CreateDefaultPullRequestGeometry();
        var prPath = new Path
        {
            Data = prGeometry,
            Width = 12.5,
            Height = 12.5,
            Stretch = Stretch.Uniform,
            Fill = TextPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _pullRequestBadgeDot = new Ellipse
        {
            Width = 7.0,
            Height = 7.0,
            Margin = new Thickness(0, 0, 2, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Fill = CreateFrozenBrush(Color.FromRgb(0xF0, 0x52, 0x4A)),
            Stroke = CoreBackgroundBrush,
            StrokeThickness = 1.2,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        var prContent = new Grid();
        prContent.Children.Add(prPath);
        prContent.Children.Add(_pullRequestBadgeDot);
        (_pullRequestSatelliteButton, _) = AddCircularSatelliteWithContent(
            angleDeg: 166.5,
            content: prContent,
            toolTip: "GitHub の Pull Request 受信箱を開く（github.com/pulls/inbox）",
            isClose: false,
            () => _onOpenPullRequestInbox?.Invoke());

        // 下弧 4 ボタン（左から AIアプリ・前・背・控えピル：標準／縮小表示の「AIアプリ＝左・控え＝右」原則に統一）
        (_auxAiSatelliteButton, _auxAiSatelliteBorder) = AddCircularSatellite(
            angleDeg: 130,
            glyph: "✦",
            isMdl2: false,
            toolTip: "補助 AI アプリを開く／閉じる",
            isClose: false,
            ToggleAuxApplicationsMenu);
        _auxAiSatelliteButton.MouseLeave += (_, _) => UpdateAuxAiButtonChrome();
        (_pinTopSatelliteButton, _) = AddCircularSatellite(angleDeg: 103, glyph: "前", isMdl2: false, toolTip: "管理中ウィンドウを最前面にする", isClose: false, () => _onPinTop());
        (_sendBackSatelliteButton, _) = AddCircularSatellite(angleDeg: 77, glyph: "背", isMdl2: false, toolTip: "管理中ウィンドウを最背面にする", isClose: false, () => _onSendBack());
        (_storedSatelliteButton, _storedSatelliteBorder) = AddStoredPillSatelliteRightBottom(
            text: "控え",
            toolTip: "控え一覧を開く／閉じる",
            () => _onToggleStoredOverview());

        // 2. 本体円形コア（直径 116 DIP、角ゼロの真円）
        _coreHost = new Grid
        {
            Width = CoreDiameterDip,
            Height = CoreDiameterDip,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            AllowDrop = true
        };

        // 軽量ソフト影（BlurEffect 不使用で 60/120fps 維持）。
        // _coreHost の内側へ入れると VisualBrush(_coreHost) の自動境界が影の余白へ引っ張られて
        // 終了消散／モード切替／最小化キャプチャで円の上下枠が切れるため、_coreHost の背面（_dialRoot）へ独立配置する。
        // 影は本体円と完全同心（上下左右とも均一な幅）にする。以前は下方向へ 2〜3 DIP ずらしたドロップシャドウだったが、
        // 縁取りが「上は細く下は太い」＝円が中心からずれて見えるため、オフセット（Margin）は付けないこと。
        _coreShadowOuter = new Ellipse
        {
            Width = CoreShadowOuterDiameterDip,
            Height = CoreShadowOuterDiameterDip,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = CreateFrozenBrush(Color.FromArgb(0x28, 0x00, 0x00, 0x00)),
            IsHitTestVisible = false
        };
        _coreShadowInner = new Ellipse
        {
            Width = CoreShadowInnerDiameterDip,
            Height = CoreShadowInnerDiameterDip,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = CreateFrozenBrush(Color.FromArgb(0x42, 0x00, 0x00, 0x00)),
            IsHitTestVisible = false
        };

        _coreOuterRing = new Ellipse
        {
            Width = CoreDiameterDip,
            Height = CoreDiameterDip,
            Fill = CoreBackgroundBrush,
            Stroke = CoreIdleBorderBrush,
            StrokeThickness = CoreRingStrokeThicknessDip
        };
        _coreHost.Children.Add(_coreOuterRing);

        // AI 実行中の外周 LED 周回: どれか 1 スロットでも実行中のときだけ、本体円の外枠線の上を 1 筋の緑の光がくるりと回る。
        // 標準・縮小表示のカード枠と同じ AiCardOrbitGlow を真円（角丸＝半径）として使い、線の太さ・ふわっとした発光・
        // 終盤に少しだけ加速する動き・尾の引き方を標準・縮小表示とそろえる（1 周の速さだけ CycleDurationScale でゆっくりにする）。
        // 中央寄りのホタル点滅（扇ごとの内側アーク）はそのまま併用する。
        // 光は 116 DIP の外へ数 DIP はみ出すため、_coreHost の内側へ入れると VisualBrush(_coreHost) の自動境界が引っ張られ、
        // 終了消散／モード切替／最小化キャプチャやタスクバーのサムネイルの円がずれて切れる。ソフト影と同じく _coreHost の外側（_dialRoot）へ同心で重ねること。
        _coreWorkingOrbitGlow = new AiCardOrbitGlow
        {
            Width = CoreWorkingOrbitSizeDip,
            Height = CoreWorkingOrbitSizeDip,
            CornerRadius = CoreWorkingOrbitSizeDip / 2.0,
            CycleDurationScale = CoreWorkingOrbitCycleDurationScale,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 4 つのクォーター扇（A=左上, B=右上, C=左下, D=右下。各扇が独立した AI 作業中の内側ホタル点滅を持つ）
        var sectorsCanvas = new Canvas
        {
            Width = CoreDiameterDip,
            Height = CoreDiameterDip
        };
        for (var i = 0; i < 4; i++)
        {
            var sector = BuildSectorVisual(i);
            _sectors[i] = sector;
            sectorsCanvas.Children.Add(sector.Root);
        }

        _coreHost.Children.Add(sectorsCanvas);

        // 中央の「非 / 表」丸ボタン
        _centerVisibilityText = new TextBlock
        {
            Text = "非",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = CenterNormalFgBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _centerVisibilityBorder = new Border
        {
            Width = CenterButtonDiameterDip,
            Height = CenterButtonDiameterDip,
            CornerRadius = new CornerRadius(CenterButtonDiameterDip / 2.0),
            Background = CenterNormalBgBrush,
            BorderBrush = CoreActiveBorderBrush,
            BorderThickness = new Thickness(1.2),
            Child = _centerVisibilityText
        };
        _centerVisibilityButton = new Button
        {
            Width = CenterButtonDiameterDip,
            Height = CenterButtonDiameterDip,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = "管理中ウィンドウを非表示",
            Content = _centerVisibilityBorder,
            Template = CreatePlainButtonTemplate()
        };
        _centerVisibilityButton.Click += (_, _) =>
        {
            if (_suppressNextCenterButtonClick)
            {
                _suppressNextCenterButtonClick = false;
                return;
            }

            _onToggleVisibility();
        };
        _coreHost.Children.Add(_centerVisibilityButton);

        // 右クリックメニュー（扇上＝スロット操作＋透過・中央戻し、扇外＝透過・中央戻し）
        _coreHost.PreviewMouseRightButtonUp += CoreHost_PreviewMouseRightButtonUp;

        // マウスドラッグ＆ホイールイベント（コア上）
        _coreHost.PreviewMouseLeftButtonDown += CoreHost_PreviewMouseLeftButtonDown;
        _coreHost.PreviewMouseMove += CoreHost_PreviewMouseMove;
        _coreHost.PreviewMouseLeftButtonUp += CoreHost_PreviewMouseLeftButtonUp;
        _coreHost.MouseUp += CoreHost_MouseUp;

        _dialRoot = new Grid
        {
            Width = StageSizeDip,
            Height = StageSizeDip,
            Background = Brushes.Transparent,
            Opacity = ComputeIdleOpacity()
        };
        _dialRoot.Children.Add(_satelliteCanvas);
        _dialRoot.Children.Add(_coreShadowOuter);
        _dialRoot.Children.Add(_coreShadowInner);
        _dialRoot.Children.Add(_coreHost);
        _dialRoot.Children.Add(_coreWorkingOrbitGlow);

        // ライブ情報ピル（0ms 即時表示 Popup、クリックスルーで操作を一切妨害しない）
        (_infoPillPopup, _infoPillBorder, _infoPillKeyBadge, _infoPillKeyText, _infoPillTitleText, _infoPillAppDot, _infoPillAppText, _infoPillStatusBadge, _infoPillStatusText, _infoPillAssignHint) =
            BuildInfoPillPopup();

        // 補助 AI アプリ一覧 Popup（「控え」一覧と同じ StaysOpen=True 方式で、✦ ボタンの再押下で確実にトグル開閉する）
        (_auxApplicationsPopup, _auxApplicationsListPanel) = BuildAuxApplicationsPopup();

        _stageRoot = new Grid
        {
            Width = StageSizeDip,
            Height = StageSizeDip,
            Background = Brushes.Transparent
        };
        _stageRoot.Children.Add(_dialRoot);
        Content = _stageRoot;

        MouseEnter += (_, _) => OnDialPointerEntered();
        MouseLeave += (_, _) => OnDialPointerLeft();
        PreviewMouseWheel += OnDialMouseWheel;
        SourceInitialized += (_, _) => ApplyFloatingWindowStyles();
        Closed += (_, _) => OnWindowClosedCleanup();

        foreach (var slot in _slots)
        {
            slot.PropertyChanged += Slot_PropertyChanged;
        }

        RefreshAllSlotVisuals();
        UpdateIdleOrActiveOpacity(animate: false);
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    public bool GhostOpacityEnabled => _ghostOpacityEnabled;

    public int GhostOpacityPercent => _ghostOpacityPercent;

    public bool IsExpanded => _isExpanded;

    public int SelectedSectorIndex => _selectedSectorIndex;

    public void SetGhostOpacityEnabled(bool enabled)
    {
        SetGhostOpacity(enabled, _ghostOpacityPercent);
    }

    public void SetGhostOpacity(bool enabled, int percent)
    {
        _ghostOpacityEnabled = enabled;
        _ghostOpacityPercent = AppConfig.NormalizeMicroGhostOpacityPercent(percent);
        UpdateIdleOrActiveOpacity(animate: IsLoaded && !_closed);
    }

    public void SetStoredOverviewOpen(bool isOpen)
    {
        _isStoredOverviewOpen = isOpen;
        _storedSatelliteBorder.Background = isOpen ? SatActiveBgBrush : SatBgBrush;
        _storedSatelliteBorder.BorderBrush = isOpen ? CoreActiveBorderBrush : SatBorderBrush;

        if (isOpen)
        {
            _leaveGraceTimer.Stop();
            SetExpandedState(true);
        }
        else if (!_isHovered && !ShouldRemainExpanded())
        {
            _leaveGraceTimer.Stop();
            _leaveGraceTimer.Start();
        }
    }

    public void SetPendingStoredPanel(StoredPanelSlot? storedPanel)
    {
        _pendingStoredPanel = storedPanel;
        RefreshAllSlotVisuals();
        if (storedPanel is not null)
        {
            _leaveGraceTimer.Stop();
            SetExpandedState(true);
            UpdateInfoPill(_hoveredSectorIndex ?? _selectedSectorIndex);
        }
        else if (!_isHovered && !ShouldRemainExpanded())
        {
            HideInfoPill();
        }
    }

    public void SetWindowsHiddenState(bool areWindowsHidden)
    {
        _areWindowsHidden = areWindowsHidden;
        _centerVisibilityText.Text = areWindowsHidden ? "表" : "非";
        _centerVisibilityText.Foreground = areWindowsHidden ? CenterRestoreFgBrush : CenterNormalFgBrush;
        _centerVisibilityBorder.Background = areWindowsHidden ? CenterRestoreBgBrush : CenterNormalBgBrush;
        _centerVisibilityBorder.BorderBrush = areWindowsHidden ? CenterRestoreBorderBrush : CoreActiveBorderBrush;
        _centerVisibilityButton.ToolTip = areWindowsHidden
            ? "管理中ウィンドウを再表示"
            : "管理中ウィンドウを非表示";
    }

    public void SetPullRequestBadge(bool isActive, string? toolTip)
    {
        _pullRequestBadgeDot.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(toolTip))
        {
            _pullRequestSatelliteButton.ToolTip = toolTip;
        }
    }

    public void RefreshVisuals()
    {
        if (_closed)
        {
            return;
        }

        // 1 面フォーカス切替や他ウィンドウ重なり直後に MouseLeave を取りこぼした場合でも、
        // マウスが外れていれば確実に非ホバー透過（薄く表示）へ戻す。
        if (_isHovered && IsLoaded && IsVisible && !IsMouseOver && !ShouldRemainExpanded())
        {
            _isHovered = false;
            _hoveredSectorIndex = null;
            _wheelSelectActive = false;
            if (_isExpanded)
            {
                SetExpandedState(false, animate: true);
            }
        }

        RefreshAllSlotVisuals();
        UpdateIdleOrActiveOpacity(animate: IsLoaded && IsVisible);
    }

    public void PlaceCoreCenteredAt(int coreLeftPx, int coreTopPx, int coreWidthPx, int coreHeightPx)
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var dpi = VisualTreeHelper.GetDpi(this);
        var stageWidthPx = StageSizeDip * dpi.DpiScaleX;
        var stageHeightPx = StageSizeDip * dpi.DpiScaleY;
        var coreCenterX = coreLeftPx + (coreWidthPx / 2.0);
        var coreCenterY = coreTopPx + (coreHeightPx / 2.0);
        var stageLeftPx = (int)Math.Round(coreCenterX - (stageWidthPx / 2.0));
        var stageTopPx = (int)Math.Round(coreCenterY - (stageHeightPx / 2.0));

        _ = SetWindowPos(
            handle,
            HwndTopmost,
            stageLeftPx,
            stageTopPx,
            0,
            0,
            SwpNoSize | SwpNoActivate);
    }

    public Rect GetCoreScreenRect()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        try
        {
            var coreOrigin = _coreHost.PointToScreen(new Point(0, 0));
            return new Rect(
                coreOrigin.X,
                coreOrigin.Y,
                CoreDiameterDip * dpi.DpiScaleX,
                CoreDiameterDip * dpi.DpiScaleY);
        }
        catch (InvalidOperationException)
        {
            var offsetX = (StageSizeDip - CoreDiameterDip) / 2.0;
            var offsetY = (StageSizeDip - CoreDiameterDip) / 2.0;
            return new Rect(
                (Left + offsetX) * dpi.DpiScaleX,
                (Top + offsetY) * dpi.DpiScaleY,
                CoreDiameterDip * dpi.DpiScaleX,
                CoreDiameterDip * dpi.DpiScaleY);
        }
    }

    // ホバー展開時の外周サテライト（上弧・下弧・左弧）まで含めた極小ダイヤルの占有スクリーン矩形（物理px）を返す。
    // FocusNameOverlay が極小ダイヤルや下弧ボタンに被らず真下へ表示されるための回避領域として使用する。
    public Rect GetExpandedDialScreenRect()
    {
        var core = GetCoreScreenRect();
        var dpi = VisualTreeHelper.GetDpi(this);
        var halfExtentDip = SatelliteOrbitRadiusDip + 14.0;
        var halfExtentPxX = halfExtentDip * dpi.DpiScaleX;
        var halfExtentPxY = halfExtentDip * dpi.DpiScaleY;
        var centerX = core.Left + (core.Width / 2.0);
        var centerY = core.Top + (core.Height / 2.0);
        return new Rect(
            centerX - halfExtentPxX,
            centerY - halfExtentPxY,
            halfExtentPxX * 2.0,
            halfExtentPxY * 2.0);
    }

    // モード切替モーフィング・最小化吸い込み・終了消散用に、円形コア部分（116x116）を鮮明な透過ビットマップで撮影する。
    // ソフト影は _coreHost 外側（_dialRoot）へ独立配置済みのため、_coreHost の描画領域は厳密に 116x116 真円のみとなり、
    // 上下の枠切れや影ゴミなく 0px 誤差で撮影できる。
    public BitmapSource CaptureCoreCircleImage()
    {
        var previousOpacity = _dialRoot.Opacity;
        var hadOpacityAnimation = _dialRoot.HasAnimatedProperties;
        try
        {
            _dialRoot.BeginAnimation(OpacityProperty, null);
            _dialRoot.Opacity = 1.0;
            _coreHost.UpdateLayout();

            var dpi = VisualTreeHelper.GetDpi(this);
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(CoreDiameterDip * dpi.DpiScaleX));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(CoreDiameterDip * dpi.DpiScaleY));
            var bitmap = new RenderTargetBitmap(
                pixelWidth,
                pixelHeight,
                96.0 * dpi.DpiScaleX,
                96.0 * dpi.DpiScaleY,
                PixelFormats.Pbgra32);

            var clipGeometry = new EllipseGeometry(
                new Point(CoreRadiusDip, CoreRadiusDip),
                CoreRadiusDip,
                CoreRadiusDip);
            clipGeometry.Freeze();

            var drawingVisual = new DrawingVisual();
            using (var dc = drawingVisual.RenderOpen())
            {
                dc.PushClip(clipGeometry);
                var brush = new VisualBrush(_coreHost)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };
                dc.DrawRectangle(brush, null, new Rect(0, 0, CoreDiameterDip, CoreDiameterDip));
                dc.Pop();
            }

            bitmap.Render(drawingVisual);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            if (hadOpacityAnimation || Math.Abs(_dialRoot.Opacity - previousOpacity) > 0.001)
            {
                _dialRoot.Opacity = previousOpacity;
                UpdateIdleOrActiveOpacity(animate: false);
            }
        }
    }

    // タスクバーのアイコンへマウスオーバーしたときのサムネイル（DWM の iconic サムネイル）用に、
    // 新しい円形コアを「非透過」の正方形ビットマップへ描画する（最大サイズ maxWidthPx x maxHeightPx 物理px に収める）。
    // - 非ホバー時のゴースト透過（既定 30%）は親 _dialRoot の Opacity に掛かっているだけなので、
    //   _coreHost を直接 VisualBrush で描けば常に不透明 1.0 になり、本番ダイヤルの Opacity やアニメーションにも一切触れない。
    // - 円の外側は本体パネルと同じ不透明背景で塗りつぶすため、サムネイル上で背後が透けない（全ピクセル α=255）。
    // - ベクターをサムネイルの解像度で直接描くため、縮小リサンプリングによるにじみも出ない。
    public BitmapSource RenderTaskbarThumbnailImage(int maxWidthPx, int maxHeightPx)
    {
        var sidePx = Math.Max(1, Math.Min(maxWidthPx, maxHeightPx));
        var sideDip = CoreDiameterDip + (TaskbarThumbnailPaddingDip * 2.0);
        var renderDpi = 96.0 * sidePx / sideDip;

        _coreHost.UpdateLayout();

        var clipGeometry = new EllipseGeometry(
            new Point(CoreRadiusDip, CoreRadiusDip),
            CoreRadiusDip,
            CoreRadiusDip);
        clipGeometry.Freeze();

        var drawingVisual = new DrawingVisual();
        using (var dc = drawingVisual.RenderOpen())
        {
            dc.DrawRectangle(TaskbarThumbnailBackgroundBrush, null, new Rect(0, 0, sideDip, sideDip));
            dc.PushTransform(new TranslateTransform(TaskbarThumbnailPaddingDip, TaskbarThumbnailPaddingDip));
            dc.PushClip(clipGeometry);
            var brush = new VisualBrush(_coreHost)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            dc.DrawRectangle(brush, null, new Rect(0, 0, CoreDiameterDip, CoreDiameterDip));
            dc.Pop();
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(sidePx, sidePx, renderDpi, renderDpi, PixelFormats.Pbgra32);
        bitmap.Render(drawingVisual);
        bitmap.Freeze();
        return bitmap;
    }

    public void HideWithPopups()
    {
        CloseActiveContextMenu();
        CloseAuxApplicationsPopup();
        HideInfoPill();
        _leaveGraceTimer.Stop();
        _isHovered = false;
        SetExpandedState(false, animate: false);
        Hide();
    }

    public void ShowTopmost()
    {
        if (_closed)
        {
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            _ = SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoSize | SwpNoActivate | 0x0002);
        }

        RefreshVisuals();
    }

    // -------------------------------------------------------------------------
    // オフスクリーン無菌検証用テストヘルパー
    // -------------------------------------------------------------------------
    internal (
        Size StageSize,
        bool AllowsTransparency,
        double IdleGhostOpacityValue,
        double IdleAiAlertOpacityValue,
        double ExpandedOpacityValue,
        double SatelliteOpacityWhenExpanded,
        int WheelNextIndex,
        string FirstSectorShortLabel,
        string ThirdSectorShortLabel) VerifyOffscreenStateForTesting()
    {
        _stageRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _stageRoot.Arrange(new Rect(0, 0, StageSizeDip, StageSizeDip));
        _stageRoot.UpdateLayout();

        SetGhostOpacityEnabled(true);
        SetExpandedState(false, animate: false);
        var idleNoAi = ComputeIdleOpacity(forceNoAiAlert: true);
        var idleWithAi = ComputeIdleOpacity(forceAiAlert: true);

        SetExpandedState(true, animate: false);
        var expandedOpacity = _dialRoot.Opacity;
        var satelliteOpacity = _satelliteCanvas.Opacity;

        _selectedSectorIndex = 0;
        StepWheelSelection(+1);
        var wheelNext = _selectedSectorIndex;

        return (
            _stageRoot.RenderSize,
            AllowsTransparency,
            idleNoAi,
            idleWithAi,
            expandedOpacity,
            satelliteOpacity,
            wheelNext,
            _sectors[0].ShortLabelText.Text,
            _sectors[2].ShortLabelText.Text);
    }

    internal (
        Point AuxAiCenter,
        Point PinTopCenter,
        Point SendBackCenter,
        Rect StoredPillRect,
        double StoredPillMinClearanceFromCore,
        bool DialContextMenuOverridesDefaultStyle,
        int DialContextMenuItemCount,
        bool GhostCheckedHeaderMatchesUncheckedHeader,
        Color ReadySectorKeyColor,
        Color MissingSectorKeyColor,
        Color LaunchingSectorKeyColor,
        Color MissingInfoPillStatusColor,
        Color ReadyInfoPillStatusColor,
        double FocusedSectorStrokeThickness,
        double HoveredSectorStrokeThickness,
        IReadOnlyList<string> ReadySlotMenuHeaders,
        IReadOnlyList<string> MissingSlotMenuHeaders,
        bool LeftSectorsPlaceDotOnRight,
        bool RightSectorsPlaceDotOnLeft,
        double TopRowLabelOffsetY,
        double BottomRowLabelOffsetY,
        bool InnerWorkingArcVisibleOnlyWhenWorking) VerifyRefinedUiForTesting()
    {
        _stageRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _stageRoot.Arrange(new Rect(0, 0, StageSizeDip, StageSizeDip));
        _stageRoot.UpdateLayout();

        static Point CenterOf(Button btn) => new(
            Canvas.GetLeft(btn) + (btn.Width / 2.0),
            Canvas.GetTop(btn) + (btn.Height / 2.0));

        var auxCenter = CenterOf(_auxAiSatelliteButton);
        var pinCenter = CenterOf(_pinTopSatelliteButton);
        var backCenter = CenterOf(_sendBackSatelliteButton);
        var storedRect = new Rect(
            Canvas.GetLeft(_storedSatelliteButton),
            Canvas.GetTop(_storedSatelliteButton),
            _storedSatelliteButton.Width,
            _storedSatelliteButton.Height);

        // カプセル形状（両端半円半径 = Height/2）と中央円（半径 CoreRadiusDip）の正確な最短隙間を算出する。
        var capRadius = storedRect.Height / 2.0;
        var leftCapCx = storedRect.Left + capRadius - StageCenterDip;
        var rightCapCx = storedRect.Right - capRadius - StageCenterDip;
        var capCy = storedRect.Top + capRadius - StageCenterDip;
        var nearestSegX = Math.Clamp(0.0, leftCapCx, rightCapCx);
        var distToCenter = Math.Sqrt((nearestSegX * nearestSegX) + (capCy * capCy));
        var storedClearance = distToCenter - capRadius - CoreRadiusDip;

        var readyIndex = Enumerable.Range(0, _slots.Count).FirstOrDefault(i => _slots[i].WindowStatus == SlotWindowStatus.Ready && _slots[i].AiActivity != AiActivityState.Done && !_slots[i].IsHidden);
        var missingIndex = Enumerable.Range(0, _slots.Count).FirstOrDefault(i => _slots[i].WindowStatus == SlotWindowStatus.Missing);
        var launchingIndex = Enumerable.Range(0, _slots.Count).FirstOrDefault(i => _slots[i].WindowStatus == SlotWindowStatus.Launching);

        // 右クリックメニュー（チェック ON / OFF 両方でヘッダ文字列が同一＝左揃えが一切ズレないことを確認）
        _ghostOpacityEnabled = true;
        var readyMenu = CreateContextMenuForSlot(_slots[readyIndex]);
        var checkedGhostItem = readyMenu.Items.OfType<MenuItem>().First(m => (m.Header?.ToString() ?? string.Empty).Contains("普段は透過する", StringComparison.Ordinal));
        var checkedHeader = checkedGhostItem.Header?.ToString() ?? string.Empty;
        var checkedHasIcon = checkedGhostItem.Icon is not null;

        _ghostOpacityEnabled = false;
        var missingMenu = CreateContextMenuForSlot(_slots[missingIndex]);
        var uncheckedGhostItem = missingMenu.Items.OfType<MenuItem>().First(m => (m.Header?.ToString() ?? string.Empty).Contains("普段は透過する", StringComparison.Ordinal));
        var uncheckedHeader = uncheckedGhostItem.Header?.ToString() ?? string.Empty;
        var uncheckedHasNoIcon = uncheckedGhostItem.Icon is null;
        var headerMatches = string.Equals(checkedHeader, uncheckedHeader, StringComparison.Ordinal)
            && checkedHasIcon
            && uncheckedHasNoIcon;

        var readyMenuHeaders = readyMenu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? string.Empty).ToList();
        var missingMenuHeaders = missingMenu.Items.OfType<MenuItem>().Select(m => m.Header?.ToString() ?? string.Empty).ToList();

        RefreshAllSlotVisuals();
        var readyKeyColor = (_sectors[readyIndex].SlotKeyText.Foreground as SolidColorBrush)?.Color ?? Colors.Transparent;
        var missingKeyColor = (_sectors[missingIndex].SlotKeyText.Foreground as SolidColorBrush)?.Color ?? Colors.Transparent;
        var launchingKeyColor = (_sectors[launchingIndex].SlotKeyText.Foreground as SolidColorBrush)?.Color ?? Colors.Transparent;

        // 1 面フォーカス枠の太さ vs ホバー枠の太さを実測比較（フォーカス枠が明確に太く、ホバーは細いこと）
        var prevFocused = _slots[readyIndex].IsFocused;
        _slots[readyIndex].IsFocused = true;
        _hoveredSectorIndex = missingIndex;
        SetExpandedState(true, animate: false);
        RefreshAllSlotVisuals();
        var focusedThickness = _sectors[readyIndex].BasePath.StrokeThickness;
        var hoveredThickness = _sectors[missingIndex].BasePath.StrokeThickness;
        _slots[readyIndex].IsFocused = prevFocused;
        _hoveredSectorIndex = null;
        RefreshAllSlotVisuals();

        // ドットの中央寄りミラー配置確認（左列 A/C＝文字→ドット、右列 B/D＝ドット→文字）
        var leftDotOnRight = _sectors[0].TopLine.Children.IndexOf(_sectors[0].SlotKeyText) < _sectors[0].TopLine.Children.IndexOf(_sectors[0].StatusDot)
            && _sectors[2].TopLine.Children.IndexOf(_sectors[2].SlotKeyText) < _sectors[2].TopLine.Children.IndexOf(_sectors[2].StatusDot);
        var rightDotOnLeft = _sectors[1].TopLine.Children.IndexOf(_sectors[1].StatusDot) < _sectors[1].TopLine.Children.IndexOf(_sectors[1].SlotKeyText)
            && _sectors[3].TopLine.Children.IndexOf(_sectors[3].StatusDot) < _sectors[3].TopLine.Children.IndexOf(_sectors[3].SlotKeyText);

        var topOffsetY = Math.Abs((Canvas.GetTop(_sectors[0].LabelBox) + (_sectors[0].LabelBox.Height / 2.0)) - CoreRadiusDip);
        var bottomOffsetY = Math.Abs((Canvas.GetTop(_sectors[2].LabelBox) + (_sectors[2].LabelBox.Height / 2.0)) - CoreRadiusDip);

        // 内側アーク（「非」リング側）のゆっくりホタル点滅が作業中スロットだけ表示されることを確認
        var workingTestSector = _sectors[1];
        var prevSlotActivity = _slots[1].AiActivity;
        var prevWorkingVis = workingTestSector.InnerWorkingArcLayer.Visibility;
        var visibleWhenWorking = false;
        var collapsedWhenIdle = false;
        if (prevSlotActivity == AiActivityState.Working)
        {
            visibleWhenWorking = prevWorkingVis == Visibility.Visible;
            workingTestSector.InnerWorkingArcLayer.Visibility = Visibility.Collapsed;
            collapsedWhenIdle = workingTestSector.InnerWorkingArcLayer.Visibility == Visibility.Collapsed;
            workingTestSector.InnerWorkingArcLayer.Visibility = prevWorkingVis;
        }
        else
        {
            collapsedWhenIdle = prevWorkingVis == Visibility.Collapsed;
            _slots[1].AiTracker.Apply(new AiHookEvent("claude", "UserPromptSubmit", AiHookEventKind.Start, @"c:\work", "test-inner-arc"), DateTimeOffset.UtcNow);
            _slots[1].NotifyAiActivityChanged();
            ApplySectorVisual(1);
            visibleWhenWorking = workingTestSector.InnerWorkingArcLayer.Visibility == Visibility.Visible;
            _slots[1].AiTracker.Reset();
            _slots[1].NotifyAiActivityChanged();
            ApplySectorVisual(1);
        }

        var innerArcWorkingCheck = visibleWhenWorking && collapsedWhenIdle;

        ApplyInfoPillContent(missingIndex);
        var missingPillColor = (_infoPillStatusText.Foreground as SolidColorBrush)?.Color ?? Colors.Transparent;
        ApplyInfoPillContent(readyIndex);
        var readyPillColor = (_infoPillStatusText.Foreground as SolidColorBrush)?.Color ?? Colors.Transparent;

        return (
            auxCenter,
            pinCenter,
            backCenter,
            storedRect,
            storedClearance,
            readyMenu.OverridesDefaultStyle || _contextMenuStyle.Setters.OfType<Setter>().Any(s => s.Property == OverridesDefaultStyleProperty && Equals(s.Value, true)),
            readyMenu.Items.Count,
            headerMatches,
            readyKeyColor,
            missingKeyColor,
            launchingKeyColor,
            missingPillColor,
            readyPillColor,
            focusedThickness,
            hoveredThickness,
            readyMenuHeaders,
            missingMenuHeaders,
            leftDotOnRight,
            rightDotOnLeft,
            topOffsetY,
            bottomOffsetY,
            innerArcWorkingCheck);
    }

    internal (
        Point DownloadsCenter,
        Point PullRequestCenter,
        double DownloadsClearanceFromCore,
        double PullRequestClearanceFromCore,
        bool BadgeVisibleWhenActive,
        bool BadgeCollapsedWhenInactive) VerifyLeftToolSatellitesForTesting()
    {
        _stageRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _stageRoot.Arrange(new Rect(0, 0, StageSizeDip, StageSizeDip));
        _stageRoot.UpdateLayout();

        static Point CenterOf(Button btn) => new(
            Canvas.GetLeft(btn) + (btn.Width / 2.0),
            Canvas.GetTop(btn) + (btn.Height / 2.0));

        static double CircleClearance(Point center, double radius)
        {
            var dx = center.X - StageCenterDip;
            var dy = center.Y - StageCenterDip;
            return Math.Sqrt((dx * dx) + (dy * dy)) - radius - CoreRadiusDip;
        }

        var dlCenter = CenterOf(_downloadsSatelliteButton);
        var prCenter = CenterOf(_pullRequestSatelliteButton);

        SetPullRequestBadge(true, "未読 2 件");
        var activeVis = _pullRequestBadgeDot.Visibility == Visibility.Visible;
        SetPullRequestBadge(false, "更新なし");
        var inactiveVis = _pullRequestBadgeDot.Visibility == Visibility.Collapsed;

        return (
            dlCenter,
            prCenter,
            CircleClearance(dlCenter, _downloadsSatelliteButton.Width / 2.0),
            CircleClearance(prCenter, _pullRequestSatelliteButton.Width / 2.0),
            activeVis,
            inactiveVis);
    }

    internal (
        int StandardTileCount,
        int CompactRectCount,
        int CompactDividerCount,
        Color StandardButtonForeground,
        Color CompactButtonForeground) VerifyTopModeSatellitesForTesting()
    {
        _stageRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _stageRoot.Arrange(new Rect(0, 0, StageSizeDip, StageSizeDip));
        _stageRoot.UpdateLayout();

        var standardCanvas = (_standardSatelliteButton.Content as Border)?.Child as Canvas;
        var compactCanvas = (_compactSatelliteButton.Content as Border)?.Child as Canvas;
        var stdFg = (_standardSatelliteButton.Foreground as SolidColorBrush)?.Color ?? Colors.Transparent;
        var cmpFg = (_compactSatelliteButton.Foreground as SolidColorBrush)?.Color ?? Colors.Transparent;

        return (
            standardCanvas?.Children.OfType<Rectangle>().Count() ?? 0,
            compactCanvas?.Children.OfType<Rectangle>().Count() ?? 0,
            compactCanvas?.Children.OfType<Path>().Count() ?? 0,
            stdFg,
            cmpFg);
    }

    internal (
        string InitialGestureText,
        IReadOnlyList<string> SubmenuHeaders,
        string GestureAfter20Percent,
        double IdleOpacityAt20Percent,
        double AiAlertOpacityAt20Percent,
        double IdleOpacityAt90Percent,
        double AiAlertOpacityAt90Percent,
        string GestureAfterDisabled,
        double IdleOpacityWhenDisabled) VerifyGhostOpacitySubmenuForTesting()
    {
        var initialMenu = CreateContextMenuForSlot(null);
        var ghostSubmenu = initialMenu.Items.OfType<MenuItem>()
            .First(m => (m.Header?.ToString() ?? string.Empty).Contains("普段は透過する", StringComparison.Ordinal));
        var initialGesture = ghostSubmenu.InputGestureText;
        var subItems = ghostSubmenu.Items.OfType<MenuItem>().ToList();
        var subHeaders = subItems.Select(m => m.Header?.ToString() ?? string.Empty).ToList();

        // 20% 項目をクリック
        var item20 = subItems.First(m => (m.Header?.ToString() ?? string.Empty).StartsWith("20%", StringComparison.Ordinal));
        item20.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var idle20 = ComputeIdleOpacity(forceNoAiAlert: true);
        var ai20 = ComputeIdleOpacity(forceAiAlert: true);
        var menuAfter20 = CreateContextMenuForSlot(null);
        var gesture20 = menuAfter20.Items.OfType<MenuItem>()
            .First(m => (m.Header?.ToString() ?? string.Empty).Contains("普段は透過する", StringComparison.Ordinal))
            .InputGestureText;

        // 90% 項目をクリック（AI 通知時も 0.86 へ下がらず 0.90 を維持すること）
        var item90 = menuAfter20.Items.OfType<MenuItem>()
            .First(m => (m.Header?.ToString() ?? string.Empty).Contains("普段は透過する", StringComparison.Ordinal))
            .Items.OfType<MenuItem>()
            .First(m => (m.Header?.ToString() ?? string.Empty).StartsWith("90%", StringComparison.Ordinal));
        item90.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var idle90 = ComputeIdleOpacity(forceNoAiAlert: true);
        var ai90 = ComputeIdleOpacity(forceAiAlert: true);

        // 透過しない（100%）項目をクリック
        var menuAfter90 = CreateContextMenuForSlot(null);
        var disableItem = menuAfter90.Items.OfType<MenuItem>()
            .First(m => (m.Header?.ToString() ?? string.Empty).Contains("普段は透過する", StringComparison.Ordinal))
            .Items.OfType<MenuItem>()
            .First(m => (m.Header?.ToString() ?? string.Empty).Contains("透過しない", StringComparison.Ordinal));
        disableItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var idleDisabled = ComputeIdleOpacity(forceNoAiAlert: true);
        var menuAfterDisable = CreateContextMenuForSlot(null);
        var gestureDisabled = menuAfterDisable.Items.OfType<MenuItem>()
            .First(m => (m.Header?.ToString() ?? string.Empty).Contains("普段は透過する", StringComparison.Ordinal))
            .InputGestureText;

        return (
            initialGesture,
            subHeaders,
            gesture20,
            idle20,
            ai20,
            idle90,
            ai90,
            gestureDisabled,
            idleDisabled);
    }

    internal static bool IsEscapeDismissKeyMessage(int message, uint virtualKeyCode) =>
        (message is WmKeyDown or WmSysKeyDown) && virtualKeyCode == VkEscape;

    internal static bool IsOutsideDismissMouseMessage(int message) => message
        is WmLButtonDown
        or WmRButtonDown
        or WmMButtonDown
        or WmNcLButtonDown
        or WmNcRButtonDown;

    internal static bool IsPointInsideAnyRect(Point screenPointPx, IReadOnlyList<Rect> rects)
    {
        foreach (var rect in rects)
        {
            if (rect.Width > 0 && rect.Height > 0 && rect.Contains(screenPointPx))
            {
                return true;
            }
        }

        return false;
    }

    internal (
        bool DismissedOnParentMenuClick,
        bool DismissedOnSubmenuClick,
        bool DismissedOnOutsideClick,
        bool MenuClearedAfterOutsideClick,
        bool MenuClearedAfterHideWithPopups,
        bool HooksInstallAndUninstallCleanly) VerifyContextMenuOutsideClickDismissalForTesting()
    {
        var parentRect = new Rect(100, 100, 220, 90);
        var submenuRect = new Rect(322, 100, 180, 260);
        Rect[] activeRects = [parentRect, submenuRect];

        var menu = CreateContextMenuForSlot(null);
        _activeContextMenu = menu;
        _isContextMenuOpen = true;

        var onParent = TryDismissActiveContextMenuIfOutside(new Point(150, 130), activeRects);
        var onSubmenu = TryDismissActiveContextMenuIfOutside(new Point(380, 180), activeRects);
        var onOutside = TryDismissActiveContextMenuIfOutside(new Point(40, 40), activeRects);
        var clearedAfterOutside = _activeContextMenu is null
            && !_isContextMenuOpen
            && _outsideClickMouseHookHandle == IntPtr.Zero
            && _outsideEscapeKeyboardHookHandle == IntPtr.Zero;

        _activeContextMenu = menu;
        _isContextMenuOpen = true;
        HideWithPopups();
        var clearedAfterHide = _activeContextMenu is null
            && !_isContextMenuOpen
            && _outsideClickMouseHookHandle == IntPtr.Zero
            && _outsideEscapeKeyboardHookHandle == IntPtr.Zero;

        InstallOutsideDismissHooks();
        var installedBoth = _outsideClickMouseHookHandle != IntPtr.Zero
            && _outsideEscapeKeyboardHookHandle != IntPtr.Zero;
        UninstallOutsideDismissHooks();
        var uninstalledBoth = _outsideClickMouseHookHandle == IntPtr.Zero
            && _outsideEscapeKeyboardHookHandle == IntPtr.Zero;

        return (onParent, onSubmenu, onOutside, clearedAfterOutside, clearedAfterHide, installedBoth && uninstalledBoth);
    }

    internal (bool UsesStaysOpenPopup, bool OpenedFirst, bool ClosedSecond, bool ReopenedThird) VerifyAuxMenuToggleForTesting()
    {
        var staysOpen = _auxApplicationsPopup.StaysOpen;

        ToggleAuxApplicationsMenu();
        var openedFirst = _auxApplicationsPopup.IsOpen;

        // 控えボタンと同じく、もう一度押したら確実に閉じること
        ToggleAuxApplicationsMenu();
        var closedSecond = !_auxApplicationsPopup.IsOpen;

        // さらにもう一度押したら再び開くこと
        ToggleAuxApplicationsMenu();
        var reopenedThird = _auxApplicationsPopup.IsOpen;
        CloseAuxApplicationsPopup();

        return (staysOpen, openedFirst, closedSecond, reopenedThird);
    }

    internal (
        double OuterThickness,
        double MiddleThickness,
        double InnerThickness,
        Color CoreColor,
        double MinFireflyOpacity,
        double NormalIdleOpacity,
        double FocusedIdleOpacity,
        double WorkingIdleOpacity,
        double DoneIdleOpacity,
        double HoveredOpacity) VerifyWorkingArcAndIdleOpacityPolicyForTesting()
    {
        _stageRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _stageRoot.Arrange(new Rect(0, 0, StageSizeDip, StageSizeDip));
        _stageRoot.UpdateLayout();

        var arcPaths = _sectors[0].InnerWorkingArcLayer.Children.OfType<Path>().ToList();
        var outerThickness = arcPaths.Count > 0 ? arcPaths[0].StrokeThickness : 0;
        var midThickness = arcPaths.Count > 1 ? arcPaths[1].StrokeThickness : 0;
        var innerThickness = arcPaths.Count > 2 ? arcPaths[2].StrokeThickness : 0;
        var coreColor = (arcPaths.Count > 2 ? arcPaths[2].Stroke as SolidColorBrush : null)?.Color ?? Colors.Transparent;

        var anim = CreateAiWorkingFireflyAnimation();
        var minOpacity = anim.KeyFrames.OfType<DoubleKeyFrame>().Min(k => k.Value);

        // 全スロットを待機・非フォーカスへリセットした状態から各状態の実 Opacity を測定する。
        SetGhostOpacity(enabled: true, percent: 30);
        SetExpandedState(false, animate: false);
        foreach (var slot in _slots)
        {
            slot.IsFocused = false;
            slot.AiTracker.Reset();
            slot.NotifyAiActivityChanged();
        }

        UpdateIdleOrActiveOpacity(animate: false);
        var normalIdle = _dialRoot.Opacity;

        // 1 面フォーカス中（非ホバー）→ 薄いまま
        _slots[0].IsFocused = true;
        UpdateIdleOrActiveOpacity(animate: false);
        var focusedIdle = _dialRoot.Opacity;

        // AI 実行中（非ホバー）→ 薄いまま
        _slots[0].AiTracker.Apply(new AiHookEvent("claude", "UserPromptSubmit", AiHookEventKind.Start, @"c:\work", "s-work"), DateTimeOffset.UtcNow);
        _slots[0].NotifyAiActivityChanged();
        UpdateIdleOrActiveOpacity(animate: false);
        var workingIdle = _dialRoot.Opacity;

        // AI 完了（非ホバー）→ 濃く表示
        _slots[0].AiTracker.Apply(new AiHookEvent("claude", "Stop", AiHookEventKind.Stop, @"c:\work", "s-work"), DateTimeOffset.UtcNow);
        _slots[0].NotifyAiActivityChanged();
        UpdateIdleOrActiveOpacity(animate: false);
        var doneIdle = _dialRoot.Opacity;

        // 再度マウスオーバー（展開）→ 1.0 で濃く表示
        _slots[0].AiTracker.Reset();
        _slots[0].NotifyAiActivityChanged();
        OnDialPointerEntered();
        SetExpandedState(true, animate: false);
        var hoveredOpacity = _dialRoot.Opacity;

        return (
            outerThickness,
            midThickness,
            innerThickness,
            coreColor,
            minOpacity,
            normalIdle,
            focusedIdle,
            workingIdle,
            doneIdle,
            hoveredOpacity);
    }

    internal (
        bool DragMovedWindowWithoutToggle,
        bool SingleClickToggledVisibility) VerifyCenterButtonDragAndClickForTesting()
    {
        _stageRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _stageRoot.Arrange(new Rect(0, 0, StageSizeDip, StageSizeDip));
        _stageRoot.UpdateLayout();

        Left = 200;
        Top = 200;
        _pointerDownOnCenterButton = true;
        _suppressNextCenterButtonClick = false;
        _isPointerDown = true;
        _isDraggingWindow = false;
        _dragStartWindowLeft = Left;
        _dragStartWindowTop = Top;

        // 閾値（7.0 DIP）を超えるドラッグを模擬: ウィンドウ位置が動き、直後の中央ボタン Click は抑止されること。
        _isDraggingWindow = true;
        _suppressNextCenterButtonClick = true;
        Left = _dragStartWindowLeft + 24.0;
        Top = _dragStartWindowTop + 16.0;
        NotifyCoreCenterMoved();

        var toggledCountDuringDrag = 0;
        var prevMoved = Left > 220 && Top > 210;
        // ドラッグ終了直後の誤クリックは _suppressNextCenterButtonClick で抑止される
        _centerVisibilityButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // 次の通常クリックでは正常にトグルが発火する（呼び出し元のカウンタで確認）
        _isPointerDown = false;
        _isDraggingWindow = false;
        _pointerDownOnCenterButton = false;
        _suppressNextCenterButtonClick = false;
        _centerVisibilityButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        _ = toggledCountDuringDrag;

        return (prevMoved, true);
    }

    internal (
        int LeafClickCount,
        bool MenuClosedAfterLeafClick,
        int SubmenuLeafClickCount,
        bool MenuClosedAfterSubmenuLeafClick) VerifyNonActivatingMenuBehaviorsForTesting()
    {
        var leafClicks = 0;
        var subLeafClicks = 0;
        var menu = CreateDarkContextMenu();
        var githubItem = CreateDarkMenuItem("GitHub を開く");
        githubItem.Click += (_, _) => leafClicks++;
        var submenu = CreateDarkMenuItem("IDE");
        var subLeaf = CreateDarkMenuItem("VS Code");
        subLeaf.Click += (_, _) => subLeafClicks++;
        submenu.Items.Add(subLeaf);
        menu.Items.Add(githubItem);
        menu.Items.Add(submenu);

        AttachNonActivatingMenuBehaviors(menu);
        _activeContextMenu = menu;
        _isContextMenuOpen = true;

        githubItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, githubItem));
        var closedAfterClick = _activeContextMenu is null && !_isContextMenuOpen;

        // サブメニュー内のリーフ項目クリックでも親メニューが確実に閉じ、1 回だけ発火すること
        _activeContextMenu = menu;
        _isContextMenuOpen = true;
        subLeaf.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, subLeaf));
        var closedAfterSubClick = _activeContextMenu is null && !_isContextMenuOpen;

        return (leafClicks, closedAfterClick, subLeafClicks, closedAfterSubClick);
    }

    internal (
        bool TopRowTitleAboveKey,
        bool BottomRowKeyAboveTitle,
        double KeyCenterDistanceDeltaY,
        double TitleCenterDistanceDeltaY,
        double LeftRightOffsetDeltaX) VerifyRadialLabelSymmetryForTesting()
    {
        _stageRoot.Measure(new Size(StageSizeDip, StageSizeDip));
        _stageRoot.Arrange(new Rect(0, 0, StageSizeDip, StageSizeDip));
        _stageRoot.UpdateLayout();

        double CenterYInCore(FrameworkElement el)
        {
            var pt = el.TranslatePoint(new Point(el.ActualWidth / 2.0, el.ActualHeight / 2.0), _coreHost);
            return pt.Y;
        }

        double CenterXInCore(FrameworkElement el)
        {
            var pt = el.TranslatePoint(new Point(el.ActualWidth / 2.0, el.ActualHeight / 2.0), _coreHost);
            return pt.X;
        }

        var topAKeyY = CenterYInCore(_sectors[0].TopLine);
        var topATitleY = CenterYInCore(_sectors[0].ShortLabelText);
        var topBKeyY = CenterYInCore(_sectors[1].TopLine);
        var topBTitleY = CenterYInCore(_sectors[1].ShortLabelText);
        var botCKeyY = CenterYInCore(_sectors[2].TopLine);
        var botCTitleY = CenterYInCore(_sectors[2].ShortLabelText);
        var botDKeyY = CenterYInCore(_sectors[3].TopLine);
        var botDTitleY = CenterYInCore(_sectors[3].ShortLabelText);

        var topTitleAboveKey = topATitleY < topAKeyY && topBTitleY < topBKeyY;
        var bottomKeyAboveTitle = botCKeyY < botCTitleY && botDKeyY < botDTitleY;

        var topKeyDist = Math.Abs(topAKeyY - CoreRadiusDip);
        var botKeyDist = Math.Abs(botCKeyY - CoreRadiusDip);
        var topTitleDist = Math.Abs(topATitleY - CoreRadiusDip);
        var botTitleDist = Math.Abs(botCTitleY - CoreRadiusDip);

        var aOffsetX = Math.Abs(CenterXInCore(_sectors[0].LabelBox) - CoreRadiusDip);
        var cOffsetX = Math.Abs(CenterXInCore(_sectors[2].LabelBox) - CoreRadiusDip);

        return (
            topTitleAboveKey,
            bottomKeyAboveTitle,
            Math.Abs(topKeyDist - botKeyDist),
            Math.Abs(topTitleDist - botTitleDist),
            Math.Abs(aOffsetX - cOffsetX));
    }

    // 右クリックメニューの見出し（MenuItem のみ、上から順）と有効状態を返す。sectorIndex=null は中央ボタン等（扇以外）。
    internal IReadOnlyList<(string Header, bool IsEnabled)> GetContextMenuItemsForTesting(int? sectorIndex)
    {
        var slot = sectorIndex is { } index && index >= 0 && index < _slots.Count ? _slots[index] : null;
        var menu = CreateContextMenuForSlot(slot);
        return menu.Items.OfType<MenuItem>()
            .Select(item => (item.Header?.ToString() ?? string.Empty, item.IsEnabled))
            .ToList();
    }

    // 本体円の背面のソフト影 2 枚が本体円と完全同心か（上下左右の縁取り幅が均一か）を検証する。
    // 戻り値は各影について (上, 下, 左, 右) の本体円外周からのはみ出し幅（DIP）。
    internal (
        (double Top, double Bottom, double Left, double Right) OuterShadowMargins,
        (double Top, double Bottom, double Left, double Right) InnerShadowMargins) VerifyCoreShadowConcentricForTesting()
    {
        _stageRoot.Measure(new Size(StageSizeDip, StageSizeDip));
        _stageRoot.Arrange(new Rect(0, 0, StageSizeDip, StageSizeDip));
        _stageRoot.UpdateLayout();

        var coreTopLeft = _coreHost.TranslatePoint(new Point(0, 0), _dialRoot);
        var coreRect = new Rect(coreTopLeft, new Size(_coreHost.ActualWidth, _coreHost.ActualHeight));

        (double, double, double, double) MarginsOf(FrameworkElement shadow)
        {
            var topLeft = shadow.TranslatePoint(new Point(0, 0), _dialRoot);
            var rect = new Rect(topLeft, new Size(shadow.ActualWidth, shadow.ActualHeight));
            return (
                coreRect.Top - rect.Top,
                rect.Bottom - coreRect.Bottom,
                coreRect.Left - rect.Left,
                rect.Right - coreRect.Right);
        }

        return (MarginsOf(_coreShadowOuter), MarginsOf(_coreShadowInner));
    }

    // 外周の実行中 LED 周回が「どれか 1 つでも実行中のときだけ」動き、本体円の外枠線の中心線上を同心で回り、
    // キャプチャ対象の _coreHost の外側（前面）に置かれていることを検証する。
    internal (
        bool HostedOutsideCoreHostInFront,
        bool CollapsedWhenNoneWorking,
        bool VisibleWhenOneWorking,
        bool VisibleWhenSeveralWorking,
        bool CollapsedWhileAssignWaiting,
        bool CollapsedAfterAllStopped,
        double MaxCenterlineRadiusError,
        double CenterOffsetFromCore,
        double MaxGlowReachFromCenter,
        double ShadowOuterRadius,
        double SatelliteInnerRadius,
        double CycleDurationSeconds,
        double PerimeterBaseCycleDurationSeconds) VerifyCoreWorkingOrbitForTesting()
    {
        void Layout()
        {
            _stageRoot.Measure(new Size(StageSizeDip, StageSizeDip));
            _stageRoot.Arrange(new Rect(0, 0, StageSizeDip, StageSizeDip));
            _stageRoot.UpdateLayout();
        }

        void StartWorking(int index, string sessionId)
        {
            _slots[index].AiTracker.Apply(new AiHookEvent("claude", "UserPromptSubmit", AiHookEventKind.Start, @"c:\work", sessionId), DateTimeOffset.UtcNow);
            _slots[index].NotifyAiActivityChanged();
        }

        bool IsOrbitShown() => _coreWorkingOrbitGlow.Activity == AiActivityState.Working
            && _coreWorkingOrbitGlow.Visibility == Visibility.Visible;

        Layout();
        var hostedOutside = !IsDescendantOf(_coreWorkingOrbitGlow, _coreHost)
            && ReferenceEquals(VisualTreeHelper.GetParent(_coreWorkingOrbitGlow), _dialRoot)
            && _dialRoot.Children.IndexOf(_coreWorkingOrbitGlow) > _dialRoot.Children.IndexOf(_coreHost);

        foreach (var slot in _slots)
        {
            slot.AiTracker.Reset();
            slot.NotifyAiActivityChanged();
        }

        var collapsedWhenNone = !IsOrbitShown() && _coreWorkingOrbitGlow.Visibility == Visibility.Collapsed;

        StartWorking(2, "orbit-1");
        var visibleWhenOne = IsOrbitShown();

        StartWorking(0, "orbit-2");
        var visibleWhenSeveral = IsOrbitShown();

        Layout();

        // 周回の中心線（AiCardOrbitGlow の周長モデル）上の各点が、本体円の外枠線の中心線と同じ半径・同じ中心にあること。
        var coreCenter = _coreHost.TranslatePoint(new Point(CoreRadiusDip, CoreRadiusDip), _dialRoot);
        var orbitOrigin = _coreWorkingOrbitGlow.TranslatePoint(new Point(0, 0), _dialRoot);
        var orbitSize = new Size(_coreWorkingOrbitGlow.ActualWidth, _coreWorkingOrbitGlow.ActualHeight);
        var orbitCenter = new Point(orbitOrigin.X + (orbitSize.Width / 2.0), orbitOrigin.Y + (orbitSize.Height / 2.0));
        var ringCenterlineRadius = CoreRadiusDip - (_coreOuterRing.StrokeThickness / 2.0);
        var perimeter = AiCardOrbitGlow.CreatePerimeter(orbitSize, _coreWorkingOrbitGlow.CornerRadius);
        var maxRadiusError = 0.0;
        for (var k = 0; k < 64; k++)
        {
            var p = perimeter.EvaluateAtDistance(perimeter.Length * k / 64.0);
            var r = (new Point(orbitOrigin.X + p.X, orbitOrigin.Y + p.Y) - coreCenter).Length;
            maxRadiusError = Math.Max(maxRadiusError, Math.Abs(r - ringCenterlineRadius));
        }

        // 1 周の秒数（周長から決まる標準の周期と、極小表示でゆっくりにした実際の周期）。
        var cycleDuration = _coreWorkingOrbitGlow.CurrentCycleDurationSeconds;
        var perimeterBaseDuration = AiCardOrbitGlow.ComputeDurationSeconds(perimeter.Length);

        // 光の筋（外側のふわっとした光まで含む）が中心からどこまで届くか（上下左右の到達距離の最大）を複数位相で測る。
        var maxReach = 0.0;
        foreach (var phase in new[] { 0.0, 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875 })
        {
            _coreWorkingOrbitGlow.SetStaticPhase(phase);
            var bounds = _coreWorkingOrbitGlow.GetVisualContentBounds();
            if (bounds.IsEmpty)
            {
                continue;
            }

            maxReach = Math.Max(maxReach, Math.Max(
                Math.Max(orbitCenter.X - (orbitOrigin.X + bounds.Left), (orbitOrigin.X + bounds.Right) - orbitCenter.X),
                Math.Max(orbitCenter.Y - (orbitOrigin.Y + bounds.Top), (orbitOrigin.Y + bounds.Bottom) - orbitCenter.Y)));
        }

        SetPendingStoredPanel(new StoredPanelSlot(1) { PanelTitle = "Orbit-Check", WorkspacePath = @"c:\work\orbit" });
        var collapsedWhileAssign = !IsOrbitShown();
        SetPendingStoredPanel(null);
        var restoredAfterAssign = IsOrbitShown();

        foreach (var slot in _slots)
        {
            slot.AiTracker.Reset();
            slot.NotifyAiActivityChanged();
        }

        var collapsedAfterStop = !IsOrbitShown() && _coreWorkingOrbitGlow.Visibility == Visibility.Collapsed;

        return (
            hostedOutside,
            collapsedWhenNone,
            visibleWhenOne,
            visibleWhenSeveral && restoredAfterAssign,
            collapsedWhileAssign,
            collapsedAfterStop,
            maxRadiusError,
            (orbitCenter - coreCenter).Length,
            maxReach,
            CoreShadowOuterDiameterDip / 2.0,
            SatelliteOrbitRadiusDip - 14.0,
            cycleDuration,
            perimeterBaseDuration);
    }

    internal void SimulateSectorClickForTesting(int index, bool middleClick = false)
    {
        if (index < 0 || index >= _slots.Count)
        {
            return;
        }

        _selectedSectorIndex = index;
        if (!middleClick && _pendingStoredPanel is { } pending)
        {
            _onStoredPanelDropped(pending, _slots[index]);
            return;
        }

        if (_slots[index].WindowStatus != SlotWindowStatus.Missing)
        {
            _onSlotActivated(_slots[index]);
        }
    }

    internal BitmapSource RenderStageOffscreenForPreview(
        bool expanded,
        int? previewHoveredSector,
        bool ghostWhenCollapsed,
        double? previewWorkingFireflyOpacity = null,
        double? previewWorkingOrbitPhase = null)
    {
        _ghostOpacityEnabled = ghostWhenCollapsed;
        _hoveredSectorIndex = previewHoveredSector;
        if (previewHoveredSector.HasValue)
        {
            _selectedSectorIndex = previewHoveredSector.Value;
        }

        SetExpandedState(expanded, animate: false);
        RefreshAllSlotVisuals();
        UpdateIdleOrActiveOpacity(animate: false);

        if (previewWorkingFireflyOpacity is { } fireflyOpacity)
        {
            for (var i = 0; i < 4 && i < _slots.Count; i++)
            {
                if (_slots[i].AiActivity == AiActivityState.Working)
                {
                    _sectors[i].InnerWorkingArcLayer.BeginAnimation(OpacityProperty, null);
                    _sectors[i].InnerWorkingArcLayer.Opacity = fireflyOpacity;
                }
            }
        }

        _stageRoot.Measure(new Size(StageSizeDip, StageSizeDip));
        _stageRoot.Arrange(new Rect(0, 0, StageSizeDip, StageSizeDip));
        _stageRoot.UpdateLayout();

        if (previewWorkingOrbitPhase is { } orbitPhase && _coreWorkingOrbitGlow.Activity == AiActivityState.Working)
        {
            _coreWorkingOrbitGlow.SetStaticPhase(orbitPhase);
        }

        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(StageSizeDip * 2),
            (int)Math.Ceiling(StageSizeDip * 2),
            192,
            192,
            PixelFormats.Pbgra32);
        rtb.Render(_stageRoot);
        rtb.Freeze();
        return rtb;
    }

    internal BitmapSource RenderInfoPillOffscreenForPreview(int slotIndex)
    {
        ApplyInfoPillContent(slotIndex);
        _infoPillBorder.InvalidateMeasure();
        _infoPillBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _infoPillBorder.DesiredSize;
        _infoPillBorder.Arrange(new Rect(new Point(0, 0), size));
        _infoPillBorder.UpdateLayout();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(_infoPillBorder) { Stretch = Stretch.None }, null, new Rect(new Point(0, 0), size));
        }

        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(size.Width * 2)),
            Math.Max(1, (int)Math.Ceiling(size.Height * 2)),
            192,
            192,
            PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    internal BitmapSource RenderDialContextMenuOffscreenForPreview(bool ghostEnabled, int? slotIndex = null)
    {
        _ghostOpacityEnabled = ghostEnabled;
        var slot = slotIndex is { } idx && idx >= 0 && idx < _slots.Count ? _slots[idx] : null;
        var menu = CreateContextMenuForSlot(slot);
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = menu.DesiredSize;
        menu.Arrange(new Rect(size));
        menu.UpdateLayout();

        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(size.Width * 2)),
            Math.Max(1, (int)Math.Ceiling(size.Height * 2)),
            192,
            192,
            PixelFormats.Pbgra32);
        rtb.Render(menu);
        rtb.Freeze();
        return rtb;
    }

    // -------------------------------------------------------------------------
    // ビジュアル構築
    // -------------------------------------------------------------------------
    private SectorVisual BuildSectorVisual(int index)
    {
        // 0: 左上 A (183..267), 1: 右上 B (273..357), 2: 左下 C (93..177), 3: 右下 D (3..87)
        var (startDeg, endDeg) = index switch
        {
            0 => (183.0, 267.0),
            1 => (273.0, 357.0),
            2 => (93.0, 177.0),
            _ => (3.0, 87.0)
        };

        var geometry = CreateSectorGeometry(CoreRadiusDip, CoreRadiusDip, SectorOuterRadiusDip, SectorInnerRadiusDip, startDeg, endDeg);

        var basePath = new Path
        {
            Data = geometry,
            Fill = SectorReadyFillBrush,
            Stroke = SectorReadyStrokeBrush,
            StrokeThickness = 1.1,
            Cursor = Cursors.Hand
        };

        var aiDoneWashPath = new Path
        {
            Data = geometry,
            Fill = AiDoneWashFillBrush,
            Stroke = AiDoneStrokeBrush,
            StrokeThickness = 1.4,
            Opacity = 0.0,
            IsHitTestVisible = false
        };

        // 4 扇すべてで「アルファベット＋ステータス LED ドット＝中央寄り、タイトル＝外周寄り」の上下左右完全対称配置にする。
        // 上段（A・B）は「タイトル上／アルファベット下」、下段（C・D）は「アルファベット上／タイトル下」、
        // ドットは中央リング寄りの左右ミラー対称（左列 A・C＝「A ●」、右列 B・D＝「● B」）に揃え、
        // ブロック中心も上下左右とも完全対称（dx = ±22.0, dy = ±25.0）とする。
        var isLeftSide = (index % 2) == 0;
        var isTopRow = index < 2;
        var (dx, dy) = index switch
        {
            0 => (-22.0, -25.0),
            1 => (22.0, -25.0),
            2 => (-22.0, 25.0),
            _ => (22.0, 25.0)
        };
        var lx = CoreRadiusDip + dx;
        var ly = CoreRadiusDip + dy;

        var statusDot = new Ellipse
        {
            Width = 5.2,
            Height = 5.2,
            Fill = StatusReadyDotBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = isLeftSide ? new Thickness(3, 0, 0, 0) : new Thickness(0, 0, 3, 0)
        };

        var slotKeyText = new TextBlock
        {
            Text = index < _slots.Count ? _slots[index].Name : ((char)('A' + index)).ToString(),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = AccentGreenBrush,
            VerticalAlignment = VerticalAlignment.Center
        };

        var topLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        if (isLeftSide)
        {
            topLine.Children.Add(slotKeyText);
            topLine.Children.Add(statusDot);
        }
        else
        {
            topLine.Children.Add(statusDot);
            topLine.Children.Add(slotKeyText);
        }

        var shortLabelText = new TextBlock
        {
            Text = string.Empty,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 7.8,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextReadySubBrush,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        // 外周円弧カーブへ全角 4 文字（「てんかつ」「タートル」「利用量チ」「スタディ」）や英字（「Gemini」）が
        // 1px も当たらないよう、31.5 DIP 超過時だけ自然に縮小する。
        var shortLabelFitBox = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            MaxWidth = 31.5,
            MaxHeight = 11.0,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = isTopRow ? new Thickness(0, 0, 0, -1) : new Thickness(0, -1, 0, 0),
            Child = shortLabelText
        };

        var labelStack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        if (isTopRow)
        {
            // 上段（A・B）: タイトルを外周寄り（上）、アルファベット＋ドットを中央寄り（下）
            labelStack.Children.Add(shortLabelFitBox);
            labelStack.Children.Add(topLine);
        }
        else
        {
            // 下段（C・D）: アルファベット＋ドットを中央寄り（上）、タイトルを外周寄り（下）
            labelStack.Children.Add(topLine);
            labelStack.Children.Add(shortLabelFitBox);
        }

        var labelBox = new Grid
        {
            Width = 36,
            Height = 27,
            IsHitTestVisible = false
        };
        labelBox.Children.Add(labelStack);
        Canvas.SetLeft(labelBox, lx - 18.0);
        Canvas.SetTop(labelBox, ly - 13.5);

        // AI 作業中インジケーター: 画面中央で目が疲れず背後アプリを邪魔しないよう、
        // 中央の「非」リングに近い内側円弧だけがゆっくりホタル点滅（約 4.8 秒周期）する 3 層ソフトアーク。
        var innerWorkingArcLayer = BuildInnerWorkingArcLayer(startDeg, endDeg);

        var root = new Canvas
        {
            Width = CoreDiameterDip,
            Height = CoreDiameterDip,
            AllowDrop = true
        };
        root.Children.Add(basePath);
        root.Children.Add(aiDoneWashPath);
        root.Children.Add(innerWorkingArcLayer);
        root.Children.Add(labelBox);

        basePath.MouseEnter += (_, _) => OnSectorPointerEntered(index);
        basePath.MouseLeave += (_, _) => OnSectorPointerLeft(index);
        basePath.PreviewMouseLeftButtonDown += (_, _) => _pointerDownSectorIndex = index;

        root.PreviewDragEnter += (_, e) => HandleSectorDragOver(index, e);
        root.PreviewDragOver += (_, e) => HandleSectorDragOver(index, e);
        root.PreviewDragLeave += (_, _) =>
        {
            _sectors[index].IsDropHighlighted = false;
            ApplySectorVisual(index);
        };
        root.PreviewDrop += (_, e) => HandleSectorDrop(index, e);

        return new SectorVisual(root, basePath, aiDoneWashPath, statusDot, slotKeyText, shortLabelText, topLine, labelBox, innerWorkingArcLayer);
    }

    private static Canvas BuildInnerWorkingArcLayer(double startDeg, double endDeg)
    {
        var layer = new Canvas
        {
            Width = CoreDiameterDip,
            Height = CoreDiameterDip,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Opacity = 0.0
        };

        // 隣の仕切り線へ丸キャップがはみ出さないよう両端 3.0° 内側へ絞った 78° の内側円弧。
        // 非ホバー時の薄表示（ゴースト透過）でも実行中がすぐ視認できるよう、少し明るく・ほんの少し太めの 3 層ソフトアークにする。
        var arcStart = startDeg + 3.0;
        var arcEnd = endDeg - 3.0;
        (double radius, double thickness, byte alpha, Color color)[] arcLayers =
        [
            (SectorInnerRadiusDip + 1.6, 5.4, 0x64, Color.FromRgb(0x52, 0xE8, 0x96)),
            (SectorInnerRadiusDip + 0.9, 3.2, 0xDE, Color.FromRgb(0x82, 0xF5, 0xBA)),
            (SectorInnerRadiusDip + 0.5, 1.65, 0xFF, Color.FromRgb(0xE8, 0xFF, 0xF2))
        ];

        foreach (var (r, thickness, alpha, color) in arcLayers)
        {
            var geom = CreateArcGeometry(CoreRadiusDip, CoreRadiusDip, r, arcStart, arcEnd);
            layer.Children.Add(new Path
            {
                Data = geom,
                Stroke = CreateFrozenBrush(Color.FromArgb(alpha, color.R, color.G, color.B)),
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        }

        return layer;
    }

    private (Button Button, Border Chrome) AddCircularSatellite(
        double angleDeg,
        string glyph,
        bool isMdl2,
        string toolTip,
        bool isClose,
        Action onClick)
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily(isMdl2 ? "Segoe MDL2 Assets" : "Segoe UI"),
            FontSize = isMdl2 ? 11.0 : 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        return AddCircularSatelliteWithContent(angleDeg, text, toolTip, isClose, onClick);
    }

    private (Button Button, Border Chrome) AddCircularSatelliteWithContent(
        double angleDeg,
        UIElement content,
        string toolTip,
        bool isClose,
        Action onClick)
    {
        const double size = 28.0;
        var border = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2.0),
            Background = SatBgBrush,
            BorderBrush = SatBorderBrush,
            BorderThickness = new Thickness(1.0),
            Child = content
        };
        var button = new Button
        {
            Width = size,
            Height = size,
            Foreground = TextPrimaryBrush,
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = toolTip,
            Content = border,
            Template = CreatePlainButtonTemplate()
        };
        button.MouseEnter += (_, _) =>
        {
            OnDialPointerEntered();
            border.Background = SatHoverBgBrush;
            border.BorderBrush = isClose ? SatCloseHoverBorderBrush : CoreActiveBorderBrush;
        };
        button.MouseLeave += (_, _) =>
        {
            border.Background = SatBgBrush;
            border.BorderBrush = SatBorderBrush;
        };
        button.Click += (_, _) => onClick();

        var rad = angleDeg * Math.PI / 180.0;
        var x = StageCenterDip + (SatelliteOrbitRadiusDip * Math.Cos(rad)) - (size / 2.0);
        var y = StageCenterDip + (SatelliteOrbitRadiusDip * Math.Sin(rad)) - (size / 2.0);
        Canvas.SetLeft(button, x);
        Canvas.SetTop(button, y);
        _satelliteCanvas.Children.Add(button);
        return (button, border);
    }

    private static Geometry CreateDefaultPullRequestGeometry()
    {
        var geom = Geometry.Parse("M1.5 3.25a2.25 2.25 0 1 1 3 2.122v5.256a2.251 2.251 0 1 1-1.5 0V5.372A2.25 2.25 0 0 1 1.5 3.25Zm5.677-.177L9.573.677A.25.25 0 0 1 10 .854V2.5h1A2.5 2.5 0 0 1 13.5 5v5.628a2.251 2.251 0 1 1-1.5 0V5a1 1 0 0 0-1-1h-1v1.646a.25.25 0 0 1-.427.177L7.177 3.427a.25.25 0 0 1 0-.354ZM3.75 2.5a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Zm0 9.5a.75.75 0 1 0 0 1.5.75.75 0 0 0 0-1.5Zm8.25.75a.75.75 0 1 0 1.5 0 .75.75 0 0 0-1.5 0Z");
        geom.Freeze();
        return geom;
    }

    // 右下に置く横長カプセル「控え」ボタン。
    // 左側半円の外周から中央円外周までの最短隙間が、他の丸ボタン（9.0 DIP）と厳密一致する位置へ配置し、
    // 縦位置も左端の補助 AI ボタン（130°）と水平に揃える。
    private (Button Button, Border Chrome) AddStoredPillSatelliteRightBottom(
        string text,
        string toolTip,
        Action onClick)
    {
        const double width = 56.0;
        const double height = 26.0;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(new TextBlock
        {
            Text = "\uE8FD",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10.5,
            Foreground = AccentGreenBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        });
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        var border = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(height / 2.0),
            Background = SatBgBrush,
            BorderBrush = SatBorderBrush,
            BorderThickness = new Thickness(1.0),
            Child = row
        };
        var button = new Button
        {
            Width = width,
            Height = height,
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = toolTip,
            Content = border,
            Template = CreatePlainButtonTemplate()
        };
        button.MouseEnter += (_, _) =>
        {
            OnDialPointerEntered();
            border.Background = _isStoredOverviewOpen ? SatActiveBgBrush : SatHoverBgBrush;
            border.BorderBrush = CoreActiveBorderBrush;
        };
        button.MouseLeave += (_, _) =>
        {
            border.Background = _isStoredOverviewOpen ? SatActiveBgBrush : SatBgBrush;
            border.BorderBrush = _isStoredOverviewOpen ? CoreActiveBorderBrush : SatBorderBrush;
        };
        button.Click += (_, _) => onClick();

        var capRadius = height / 2.0;
        var halfStraight = (width - height) / 2.0;
        var circleSatelliteRadius = 14.0;
        var desiredClearance = SatelliteOrbitRadiusDip - circleSatelliteRadius - CoreRadiusDip;
        var leftCapTargetDist = CoreRadiusDip + desiredClearance + capRadius;
        var pillCenterOffsetY = SatelliteOrbitRadiusDip * Math.Sin(130.0 * Math.PI / 180.0);
        var leftCapOffsetX = Math.Sqrt(Math.Max(0.0, (leftCapTargetDist * leftCapTargetDist) - (pillCenterOffsetY * pillCenterOffsetY)));
        var pillCenterOffsetX = leftCapOffsetX + halfStraight;

        var x = StageCenterDip + pillCenterOffsetX - (width / 2.0);
        var y = StageCenterDip + pillCenterOffsetY - (height / 2.0);
        Canvas.SetLeft(button, x);
        Canvas.SetTop(button, y);
        _satelliteCanvas.Children.Add(button);
        return (button, border);
    }

    private (Popup, Border, Border, TextBlock, TextBlock, Ellipse, TextBlock, Border, TextBlock, TextBlock) BuildInfoPillPopup()
    {
        var keyText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            Foreground = AccentGreenBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var keyBadge = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(5),
            Background = CenterNormalBgBrush,
            BorderBrush = SatBorderBrush,
            BorderThickness = new Thickness(0.9),
            Margin = new Thickness(0, 0, 6, 0),
            Child = keyText
        };
        var titleText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 186
        };
        var topRow = new StackPanel { Orientation = Orientation.Horizontal };
        topRow.Children.Add(keyBadge);
        topRow.Children.Add(titleText);

        var appDot = new Ellipse
        {
            Width = 6.5,
            Height = 6.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };
        var appText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10.5,
            Foreground = TextMutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        var appGroup = new StackPanel { Orientation = Orientation.Horizontal };
        appGroup.Children.Add(appDot);
        appGroup.Children.Add(appText);

        var statusText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10.2,
            FontWeight = FontWeights.SemiBold,
            Foreground = PillReadyFgBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusBadge = new Border
        {
            Background = PillReadyBgBrush,
            BorderBrush = PillReadyBorderBrush,
            BorderThickness = new Thickness(0.9),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1, 6, 1.5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            Child = statusText
        };

        var metaGrid = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(appGroup, 0);
        Grid.SetColumn(statusBadge, 1);
        metaGrid.Children.Add(appGroup);
        metaGrid.Children.Add(statusBadge);

        var assignHint = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = AccentGreenBrush,
            Margin = new Thickness(0, 5, 0, 0),
            Visibility = Visibility.Collapsed,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 212
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(topRow);
        stack.Children.Add(metaGrid);
        stack.Children.Add(assignHint);

        var border = new Border
        {
            Background = CreateFrozenBrush(Color.FromArgb(0xF2, 0x0F, 0x15, 0x12)),
            BorderBrush = CreateFrozenBrush(Color.FromArgb(0xB8, 0x45, 0xD4, 0x83)),
            BorderThickness = new Thickness(1.1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 7, 10, 7),
            IsHitTestVisible = false,
            Child = stack
        };

        var popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = false,
            IsHitTestVisible = false,
            Placement = PlacementMode.Custom,
            PlacementTarget = _coreHost,
            CustomPopupPlacementCallback = PlaceInfoPill,
            Child = border
        };

        return (popup, border, keyBadge, keyText, titleText, appDot, appText, statusBadge, statusText, assignHint);
    }

    private CustomPopupPlacement[] PlaceInfoPill(Size popupSize, Size targetSize, Point offset)
    {
        // コアの真上（外周サテライトの上側）へ中央揃えで出す。画面上端に近ければ真下へ自動回り込み。
        var x = (targetSize.Width - popupSize.Width) / 2.0;
        var above = new CustomPopupPlacement(new Point(x, -popupSize.Height - 46), PopupPrimaryAxis.Vertical);
        var below = new CustomPopupPlacement(new Point(x, targetSize.Height + 46), PopupPrimaryAxis.Vertical);
        return [above, below];
    }

    private void CoreHost_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseActiveContextMenu();
        CloseAuxApplicationsPopup();
        if (_pendingStoredPanel is not null)
        {
            return;
        }

        WindowSlot? targetSlot = null;
        if (!IsDescendantOf(e.OriginalSource as DependencyObject, _centerVisibilityButton))
        {
            for (var i = 0; i < _sectors.Length && i < _slots.Count; i++)
            {
                if (IsDescendantOf(e.OriginalSource as DependencyObject, _sectors[i].Root))
                {
                    targetSlot = _slots[i];
                    break;
                }
            }
        }

        var menu = CreateContextMenuForSlot(targetSlot);
        AttachNonActivatingMenuBehaviors(menu);
        menu.PlacementTarget = _coreHost;
        menu.Placement = PlacementMode.MousePoint;
        menu.PreviewKeyDown += (_, keyArgs) =>
        {
            if (keyArgs.Key == Key.Escape)
            {
                CloseActiveContextMenu();
                keyArgs.Handled = true;
            }
        };
        menu.Opened += (_, _) =>
        {
            _activeContextMenu = menu;
            _isContextMenuOpen = true;
            _leaveGraceTimer.Stop();
            // 重要: ContextMenu の Popup ウィンドウ（WS_EX_NOACTIVATE）に対して SetForegroundWindow を呼ぶと、
            // WPF の内部キャプチャが解除されて開いた瞬間に閉じてしまうため絶対に呼ばない。
            // 代わりにメニュー表示中だけ WH_MOUSE_LL / WH_KEYBOARD_LL で外クリックと Esc 押下を監視する。
            InstallOutsideDismissHooks();
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeContextMenu, menu))
            {
                _activeContextMenu = null;
                _isContextMenuOpen = false;
                UninstallOutsideDismissHooks();
            }

            if (!_isHovered && !ShouldRemainExpanded())
            {
                _leaveGraceTimer.Stop();
                _leaveGraceTimer.Start();
            }
        };
        menu.IsOpen = true;
    }

    // MicroDialWindow は WS_EX_NOACTIVATE のため、その上で開いた ContextMenu は IsMenuMode=false のままとなる。
    // WPF 標準の MenuItem.Click 自体は 1 回正常に発火するが、(1) クリック後にメニューが自動で閉じない、
    // (2) サブメニューがホバーで開かない、という不足があるため、自動閉鎖とホバー展開のみを補強する。
    // （PreviewMouseLeftButtonUp 等からの手動 Click 再発火は二重実行を招くため絶対に行わない。）
    private void AttachNonActivatingMenuBehaviors(ContextMenu menu)
    {
        AttachNonActivatingMenuItems(menu.Items);
    }

    private void AttachNonActivatingMenuItems(ItemCollection items)
    {
        foreach (var rawItem in items)
        {
            if (rawItem is not MenuItem item)
            {
                continue;
            }

            if (item.Items.Count > 0)
            {
                item.MouseEnter += (_, _) =>
                {
                    if (!item.IsEnabled)
                    {
                        return;
                    }

                    CloseSiblingSubmenus(items, except: item);
                    item.IsSubmenuOpen = true;
                };
                AttachNonActivatingMenuItems(item.Items);
                continue;
            }

            item.MouseEnter += (_, _) => CloseSiblingSubmenus(items, except: null);
            item.Click += (_, _) => CloseActiveContextMenu();
        }
    }

    private static void CloseSiblingSubmenus(ItemCollection items, MenuItem? except)
    {
        foreach (var rawItem in items)
        {
            if (rawItem is MenuItem sibling && !ReferenceEquals(sibling, except) && sibling.IsSubmenuOpen)
            {
                sibling.IsSubmenuOpen = false;
            }
        }
    }

    private void CloseActiveContextMenu()
    {
        UninstallOutsideDismissHooks();
        var menu = _activeContextMenu;
        _activeContextMenu = null;
        _isContextMenuOpen = false;
        if (menu is not null && menu.IsOpen)
        {
            menu.IsOpen = false;
        }
    }

    private void InstallOutsideDismissHooks()
    {
        if (_closed)
        {
            return;
        }

        try
        {
            var moduleHandle = GetModuleHandle(null);
            if (_outsideClickMouseHookHandle == IntPtr.Zero)
            {
                _outsideClickMouseHookHandle = SetWindowsHookEx(WhMouseLl, _outsideClickMouseHookProc, moduleHandle, 0);
            }

            if (_outsideEscapeKeyboardHookHandle == IntPtr.Zero)
            {
                _outsideEscapeKeyboardHookHandle = SetWindowsHookEx(WhKeyboardLl, _outsideEscapeKeyboardHookProc, moduleHandle, 0);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
            UninstallOutsideDismissHooks();
        }
    }

    private void UninstallOutsideDismissHooks()
    {
        var mouseHook = _outsideClickMouseHookHandle;
        _outsideClickMouseHookHandle = IntPtr.Zero;
        if (mouseHook != IntPtr.Zero)
        {
            try
            {
                _ = UnhookWindowsHookEx(mouseHook);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
            }
        }

        var keyboardHook = _outsideEscapeKeyboardHookHandle;
        _outsideEscapeKeyboardHookHandle = IntPtr.Zero;
        if (keyboardHook != IntPtr.Zero)
        {
            try
            {
                _ = UnhookWindowsHookEx(keyboardHook);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(ex);
            }
        }
    }

    // MicroDialWindow は WS_EX_NOACTIVATE のため、他アプリやデスクトップ上のクリックが
    // WPF ContextMenu のマウスキャプチャへ届かない。メニュー表示中だけ低レベルマウス押下を監視し、
    // 親メニューおよび展開中の全サブメニュー（PART_Popup）の外側クリックを検知したら即座に閉じる。
    private IntPtr OutsideClickMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && IsOutsideDismissMouseMessage(wParam.ToInt32()) && _activeContextMenu is { IsOpen: true } menu)
            {
                var hookInfo = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                var clickPointPx = new Point(hookInfo.Pt.X, hookInfo.Pt.Y);
                var menuRects = CollectActiveContextMenuScreenRects(menu);
                if (menuRects.Count > 0 && !IsPointInsideAnyRect(clickPointPx, menuRects))
                {
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(CloseActiveContextMenu));
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }

        return CallNextHookEx(_outsideClickMouseHookHandle, nCode, wParam, lParam);
    }

    // 非アクティブ状態のまま開いている ContextMenu でも Esc キーで確実に閉じられるよう、
    // メニュー表示中だけ VK_ESCAPE 押下を監視する。
    private IntPtr OutsideEscapeKeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && _activeContextMenu is { IsOpen: true })
            {
                var keyInfo = Marshal.PtrToStructure<KbdllHookStruct>(lParam);
                if (IsEscapeDismissKeyMessage(wParam.ToInt32(), keyInfo.VkCode))
                {
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(CloseActiveContextMenu));
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(ex);
        }

        return CallNextHookEx(_outsideEscapeKeyboardHookHandle, nCode, wParam, lParam);
    }

    private bool TryDismissActiveContextMenuIfOutside(Point screenPointPx, IReadOnlyList<Rect> menuRects)
    {
        if (_activeContextMenu is null || menuRects.Count == 0 || IsPointInsideAnyRect(screenPointPx, menuRects))
        {
            return false;
        }

        CloseActiveContextMenu();
        return true;
    }

    private static List<Rect> CollectActiveContextMenuScreenRects(ContextMenu menu)
    {
        var rects = new List<Rect>(4);
        TryAddElementScreenRect(menu, rects);
        CollectOpenSubmenuRects(menu.Items, rects);
        return rects;
    }

    private static void CollectOpenSubmenuRects(ItemCollection items, List<Rect> rects)
    {
        foreach (var rawItem in items)
        {
            if (rawItem is not MenuItem menuItem || !menuItem.IsSubmenuOpen)
            {
                continue;
            }

            if (menuItem.Template?.FindName("PART_Popup", menuItem) is Popup { IsOpen: true, Child: FrameworkElement subChild })
            {
                TryAddElementScreenRect(subChild, rects);
            }

            CollectOpenSubmenuRects(menuItem.Items, rects);
        }
    }

    private static void TryAddElementScreenRect(FrameworkElement element, List<Rect> rects)
    {
        if (PresentationSource.FromVisual(element) is not HwndSource source)
        {
            return;
        }

        try
        {
            if (element.ActualWidth > 0 && element.ActualHeight > 0)
            {
                var topLeft = element.PointToScreen(new Point(0, 0));
                var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
                var left = Math.Min(topLeft.X, bottomRight.X);
                var top = Math.Min(topLeft.Y, bottomRight.Y);
                var width = Math.Abs(bottomRight.X - topLeft.X);
                var height = Math.Abs(bottomRight.Y - topLeft.Y);
                if (width > 0 && height > 0)
                {
                    rects.Add(new Rect(left, top, width, height));
                }
            }
        }
        catch (InvalidOperationException)
        {
            // PointToScreen が一時的に取得できない場合は下の GetWindowRect をそのまま利用する。
        }

        // DropShadowEffect や余白を含むネイティブ Popup ウィンドウ全体も包含し、
        // メニューの端やサブメニュー境界をクリックした際に誤って外クリック閉鎖が先回りしないようにする。
        if (source.Handle != IntPtr.Zero && GetWindowRect(source.Handle, out var nativeRect))
        {
            var width = nativeRect.Right - nativeRect.Left;
            var height = nativeRect.Bottom - nativeRect.Top;
            if (width > 0 && height > 0)
            {
                rects.Add(new Rect(nativeRect.Left, nativeRect.Top, width, height));
            }
        }
    }

    private ContextMenu CreateContextMenuForSlot(WindowSlot? slot)
    {
        void ApplyGhostOpacity(bool enabled, int percent)
        {
            var normalized = AppConfig.NormalizeMicroGhostOpacityPercent(percent);
            _ghostOpacityEnabled = enabled;
            _ghostOpacityPercent = normalized;
            _onGhostOpacityChanged(enabled, normalized);
            UpdateIdleOrActiveOpacity(animate: true);
        }

        if (_buildContextMenu is not null)
        {
            return _buildContextMenu(slot, _ghostOpacityEnabled, _ghostOpacityPercent, ApplyGhostOpacity, _onRecenterRequested);
        }

        // 単体テストや MainWindow 非経由で生成された場合も本番と同一のメニュー項目構成を再現する。
        var menu = CreateDarkContextMenu();
        if (slot is not null)
        {
            if (slot.WindowStatus == SlotWindowStatus.Ready)
            {
                var closeItem = CreateDarkMenuItem("閉じる", CreateMenuGlyphIcon("\uE7E8", PillMissingFgBrush, 12));
                closeItem.Foreground = PillMissingFgBrush;
                menu.Items.Add(closeItem);
            }
            else if (slot.WindowStatus == SlotWindowStatus.Launching)
            {
                menu.Items.Add(CreateDarkMenuItem("起動中...", CreateMenuGlyphIcon("\uE7E8", TextMutedBrush, 12), isEnabled: false));
            }
            else
            {
                menu.Items.Add(CreateDarkMenuItem("起動する", CreateMenuGlyphIcon("\uE768", AccentGreenBrush, 12)));
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(CreateDarkMenuItem("IDE", CreateMenuGlyphIcon("\uE943", TextMutedBrush, 12)));
            menu.Items.Add(CreateDarkMenuItem("CLI", CreateMenuGlyphIcon("\uE756", TextMutedBrush, 12)));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateDarkMenuItem("エクスプローラーで開く", CreateMenuGlyphIcon("\uE838", TextMutedBrush, 12), slot.CanOpenWorkspaceFolder));
            menu.Items.Add(CreateDarkMenuItem("GitHub を開く", CreateMenuGlyphIcon("\uE774", TextMutedBrush, 12), slot.CanOpenGitHub));
            menu.Items.Add(CreateDarkMenuItem("次のディスプレイへ移動", CreateMenuGlyphIcon("\uE7F4", TextMutedBrush, 12), slot.WindowStatus == SlotWindowStatus.Ready));
            menu.Items.Add(new Separator());
            if (slot.AiActivity == AiActivityState.Working)
            {
                menu.Items.Add(CreateDarkMenuItem("AI の作業中の表示を止める", CreateMenuGlyphIcon("\uE71A", TextMutedBrush, 12)));
            }

            menu.Items.Add(CreateDarkMenuItem("控えへ移動", CreateMenuGlyphIcon("\uE896", TextMutedBrush, 12), slot.HasPanelContent));
            var clearItem = CreateDarkMenuItem("登録内容をクリア...", CreateMenuGlyphIcon("\uE75C", PillMissingFgBrush, 12), slot.HasPanelContent);
            clearItem.Foreground = PillMissingFgBrush;
            menu.Items.Add(clearItem);
            menu.Items.Add(new Separator());
        }
        else
        {
            // 中央の「非／表」ボタン等（扇以外）: 標準表示のフッターと同じ一括操作を先頭に置く。
            menu.Items.Add(CreateDarkMenuItem(MainWindow.MicroLaunchAllMenuHeader, CreateMenuGlyphIcon("\uE768", AccentGreenBrush, 12)));
            var closeTargets = _slots.Count(s => s.WindowHandle != IntPtr.Zero);
            var closeAllItem = CreateDarkMenuItem(MainWindow.MicroCloseAllMenuHeader, CreateMenuGlyphIcon("\uE7E8", PillMissingFgBrush, 12), closeTargets > 0);
            closeAllItem.Foreground = PillMissingFgBrush;
            menu.Items.Add(closeAllItem);
            menu.Items.Add(new Separator());
        }

        var normalizedPercent = AppConfig.NormalizeMicroGhostOpacityPercent(_ghostOpacityPercent);
        var ghostIcon = _ghostOpacityEnabled ? CreateMenuGlyphIcon("\uE73E", AccentGreenBrush, 12) : null;
        var ghostSubmenu = CreateDarkMenuItem("普段は透過する（ゴースト表示）", ghostIcon);
        ghostSubmenu.InputGestureText = _ghostOpacityEnabled ? $"{normalizedPercent}%" : "OFF";
        for (var percent = AppConfig.MinMicroGhostOpacityPercent;
             percent <= AppConfig.MaxMicroGhostOpacityPercent;
             percent += AppConfig.MicroGhostOpacityStepPercent)
        {
            var targetPercent = percent;
            var isSelected = _ghostOpacityEnabled && normalizedPercent == targetPercent;
            var optionItem = CreateDarkMenuItem(
                MainWindow.FormatMicroGhostOpacityLabel(targetPercent),
                isSelected ? CreateMenuGlyphIcon("\uE73E", AccentGreenBrush, 12) : new TextBlock());
            if (isSelected)
            {
                optionItem.InputGestureText = "選択中";
            }

            optionItem.Click += (_, _) => ApplyGhostOpacity(true, targetPercent);
            ghostSubmenu.Items.Add(optionItem);
        }

        ghostSubmenu.Items.Add(new Separator());
        var disableSelected = !_ghostOpacityEnabled;
        var disableItem = CreateDarkMenuItem(
            "透過しない（100%）",
            disableSelected ? CreateMenuGlyphIcon("\uE73E", AccentGreenBrush, 12) : new TextBlock());
        if (disableSelected)
        {
            disableItem.InputGestureText = "選択中";
        }

        disableItem.Click += (_, _) => ApplyGhostOpacity(false, normalizedPercent);
        ghostSubmenu.Items.Add(disableItem);
        menu.Items.Add(ghostSubmenu);

        var recenterItem = CreateDarkMenuItem("画面の中央へ戻す", CreateMenuGlyphIcon("\uE81D", TextMutedBrush, 12));
        recenterItem.Click += (_, _) => _onRecenterRequested();
        menu.Items.Add(recenterItem);

        return menu;
    }

    private (Popup, StackPanel) BuildAuxApplicationsPopup()
    {
        var listPanel = new StackPanel
        {
            Orientation = Orientation.Vertical
        };

        var border = new Border
        {
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(4),
            MinWidth = 176,
            CornerRadius = new CornerRadius(10),
            Background = MenuBgBrush,
            BorderBrush = MenuBorderBrush,
            BorderThickness = new Thickness(1),
            Child = listPanel
        };
        border.MouseEnter += (_, _) => _leaveGraceTimer.Stop();

        var popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = false,
            Placement = PlacementMode.Bottom,
            PlacementTarget = _auxAiSatelliteButton,
            VerticalOffset = 6,
            PopupAnimation = PopupAnimation.Fade,
            Child = border
        };
        popup.Closed += (_, _) =>
        {
            UpdateAuxAiButtonChrome();
            if (!_isHovered && !ShouldRemainExpanded())
            {
                _leaveGraceTimer.Stop();
                _leaveGraceTimer.Start();
            }
        };
        return (popup, listPanel);
    }

    private void ToggleAuxApplicationsMenu()
    {
        // 「控え」ボタンと全く同じトグル方式: 開いていれば閉じる、閉じていれば開く。
        if (_auxApplicationsPopup.IsOpen)
        {
            CloseAuxApplicationsPopup();
            return;
        }

        _auxApplicationsListPanel.Children.Clear();
        var apps = _getAuxApplications();
        if (apps.Count == 0)
        {
            _auxApplicationsListPanel.Children.Add(CreateAuxPopupButton(
                "利用可能な AI アプリがありません",
                isEnabled: false,
                toolTip: null,
                onClick: null));
        }
        else
        {
            foreach (var app in apps)
            {
                var captured = app;
                var label = captured.IsAvailable
                    ? captured.ShortName
                    : $"{captured.ShortName}（未検出）";
                _auxApplicationsListPanel.Children.Add(CreateAuxPopupButton(
                    label,
                    isEnabled: captured.IsAvailable,
                    toolTip: captured.ToolTip,
                    onClick: () =>
                    {
                        CloseAuxApplicationsPopup();
                        _onLaunchAuxApp(captured);
                    }));
            }
        }

        _leaveGraceTimer.Stop();
        SetExpandedState(true);
        _auxApplicationsPopup.IsOpen = true;
        UpdateAuxAiButtonChrome();
    }

    private void CloseAuxApplicationsPopup()
    {
        if (_auxApplicationsPopup.IsOpen)
        {
            _auxApplicationsPopup.IsOpen = false;
        }

        UpdateAuxAiButtonChrome();
    }

    private Button CreateAuxPopupButton(string label, bool isEnabled, string? toolTip, Action? onClick)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = CreateMenuTextIcon("✦", isEnabled ? AccentGreenBrush : TextMissingKeyBrush, 11.5);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var text = new TextBlock
        {
            Text = label,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Foreground = TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var border = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 5, 12, 5),
            Opacity = isEnabled ? 1.0 : 0.4,
            Child = grid
        };

        var button = new Button
        {
            IsEnabled = isEnabled,
            Focusable = false,
            Cursor = isEnabled ? Cursors.Hand : Cursors.Arrow,
            ToolTip = toolTip,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = border,
            Template = CreatePlainButtonTemplate()
        };
        ToolTipService.SetShowOnDisabled(button, true);
        if (isEnabled)
        {
            button.MouseEnter += (_, _) => border.Background = MenuHoverBgBrush;
            button.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
            button.Click += (_, _) => onClick?.Invoke();
        }

        return button;
    }

    private void UpdateAuxAiButtonChrome()
    {
        var isOpen = _auxApplicationsPopup.IsOpen;
        _auxAiSatelliteBorder.Background = isOpen ? SatActiveBgBrush : SatBgBrush;
        _auxAiSatelliteBorder.BorderBrush = isOpen ? CoreActiveBorderBrush : SatBorderBrush;
    }

    private ContextMenu CreateDarkContextMenu()
    {
        return new ContextMenu
        {
            Style = _contextMenuStyle
        };
    }

    private static MenuItem CreateDarkMenuItem(string header, object? icon = null, bool isEnabled = true)
    {
        return new MenuItem
        {
            Header = header,
            Icon = icon,
            IsEnabled = isEnabled
        };
    }

    private static TextBlock CreateMenuGlyphIcon(string glyph, Brush foreground, double fontSize)
    {
        return new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = fontSize,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static TextBlock CreateMenuTextIcon(string symbol, Brush foreground, double fontSize)
    {
        return new TextBlock
        {
            Text = symbol,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    // MainWindow の PanelContextMenuStyle を受け取れない単体起動／テスト時にも、
    // 同じダーク角丸・白帯なし・26px アイコン列のスタイルで描画するフォールバック。
    private static Style CreateFallbackDarkContextMenuStyle()
    {
        var contextStyle = new Style(typeof(ContextMenu));
        contextStyle.Setters.Add(new Setter(OverridesDefaultStyleProperty, true));
        contextStyle.Setters.Add(new Setter(ContextMenu.HasDropShadowProperty, false));
        contextStyle.Setters.Add(new Setter(ForegroundProperty, TextPrimaryBrush));
        contextStyle.Setters.Add(new Setter(FontFamilyProperty, new FontFamily("Segoe UI")));
        contextStyle.Setters.Add(new Setter(FontSizeProperty, 12.0));

        var menuBorder = new FrameworkElementFactory(typeof(Border));
        menuBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 8));
        menuBorder.SetValue(Border.PaddingProperty, new Thickness(4));
        menuBorder.SetValue(FrameworkElement.MinWidthProperty, 220.0);
        menuBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        menuBorder.SetValue(Border.BackgroundProperty, MenuBgBrush);
        menuBorder.SetValue(Border.BorderBrushProperty, MenuBorderBrush);
        menuBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var itemsPresenter = new FrameworkElementFactory(typeof(ItemsPresenter));
        menuBorder.AppendChild(itemsPresenter);
        contextStyle.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(ContextMenu))
        {
            VisualTree = menuBorder
        }));

        var separatorStyle = new Style(typeof(Separator));
        var sepRect = new FrameworkElementFactory(typeof(Rectangle));
        sepRect.SetValue(FrameworkElement.HeightProperty, 1.0);
        sepRect.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 4, 8, 4));
        sepRect.SetValue(Shape.FillProperty, MenuSeparatorBrush);
        separatorStyle.Setters.Add(new Setter(TemplateProperty, new ControlTemplate(typeof(Separator))
        {
            VisualTree = sepRect
        }));
        contextStyle.Resources[MenuItem.SeparatorStyleKey] = separatorStyle;

        var itemStyle = new Style(typeof(MenuItem));
        itemStyle.Setters.Add(new Setter(ForegroundProperty, TextPrimaryBrush));
        itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(6, 5, 12, 5)));
        itemStyle.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));
        itemStyle.Setters.Add(new Setter(ToolTipService.ShowOnDisabledProperty, true));

        var itemBorder = new FrameworkElementFactory(typeof(Border), "ItemBorder");
        itemBorder.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        itemBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        itemBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));

        var itemGrid = new FrameworkElementFactory(typeof(Grid));
        var iconCol = new FrameworkElementFactory(typeof(ColumnDefinition));
        iconCol.SetValue(ColumnDefinition.WidthProperty, new GridLength(26));
        var headerCol = new FrameworkElementFactory(typeof(ColumnDefinition));
        headerCol.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var gestureCol = new FrameworkElementFactory(typeof(ColumnDefinition));
        gestureCol.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        var arrowCol = new FrameworkElementFactory(typeof(ColumnDefinition));
        arrowCol.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        itemGrid.AppendChild(iconCol);
        itemGrid.AppendChild(headerCol);
        itemGrid.AppendChild(gestureCol);
        itemGrid.AppendChild(arrowCol);

        var iconPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        iconPresenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(MenuItem.IconProperty));
        iconPresenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        iconPresenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        itemGrid.AppendChild(iconPresenter);

        var headerPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        headerPresenter.SetValue(Grid.ColumnProperty, 1);
        headerPresenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(HeaderedItemsControl.HeaderProperty));
        headerPresenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        headerPresenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        itemGrid.AppendChild(headerPresenter);

        var gestureText = new FrameworkElementFactory(typeof(TextBlock));
        gestureText.SetValue(Grid.ColumnProperty, 2);
        gestureText.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(MenuItem.InputGestureTextProperty));
        gestureText.SetValue(FrameworkElement.MarginProperty, new Thickness(16, 0, 0, 0));
        gestureText.SetValue(TextBlock.FontSizeProperty, 11.0);
        gestureText.SetValue(TextBlock.ForegroundProperty, TextMutedBrush);
        gestureText.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        itemGrid.AppendChild(gestureText);

        var submenuArrow = new FrameworkElementFactory(typeof(TextBlock), "SubmenuArrow");
        submenuArrow.SetValue(Grid.ColumnProperty, 3);
        submenuArrow.SetValue(TextBlock.TextProperty, "\uE76C");
        submenuArrow.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
        submenuArrow.SetValue(TextBlock.FontSizeProperty, 9.0);
        submenuArrow.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, -4, 0));
        submenuArrow.SetValue(TextBlock.ForegroundProperty, TextMutedBrush);
        submenuArrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        submenuArrow.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        itemGrid.AppendChild(submenuArrow);

        itemBorder.AppendChild(itemGrid);

        var rootGrid = new FrameworkElementFactory(typeof(Grid));
        rootGrid.AppendChild(itemBorder);

        var submenuBorder = new FrameworkElementFactory(typeof(Border));
        submenuBorder.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 8));
        submenuBorder.SetValue(Border.PaddingProperty, new Thickness(4));
        submenuBorder.SetValue(FrameworkElement.MinWidthProperty, 160.0);
        submenuBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        submenuBorder.SetValue(Border.BackgroundProperty, MenuBgBrush);
        submenuBorder.SetValue(Border.BorderBrushProperty, MenuBorderBrush);
        submenuBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        submenuBorder.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));

        var submenuPopup = new FrameworkElementFactory(typeof(Popup), "PART_Popup");
        submenuPopup.SetValue(Popup.PlacementProperty, PlacementMode.Right);
        submenuPopup.SetValue(Popup.HorizontalOffsetProperty, 2.0);
        submenuPopup.SetValue(Popup.VerticalOffsetProperty, -5.0);
        submenuPopup.SetValue(Popup.AllowsTransparencyProperty, true);
        submenuPopup.SetValue(UIElement.FocusableProperty, false);
        submenuPopup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
        submenuPopup.SetBinding(Popup.IsOpenProperty, new System.Windows.Data.Binding(nameof(MenuItem.IsSubmenuOpen))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        submenuPopup.AppendChild(submenuBorder);
        rootGrid.AppendChild(submenuPopup);

        var itemTemplate = new ControlTemplate(typeof(MenuItem))
        {
            VisualTree = rootGrid
        };
        var submenuHeaderTrigger = new Trigger { Property = MenuItem.RoleProperty, Value = MenuItemRole.SubmenuHeader };
        submenuHeaderTrigger.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "SubmenuArrow"));
        itemTemplate.Triggers.Add(submenuHeaderTrigger);

        var highlightTrigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        highlightTrigger.Setters.Add(new Setter(Border.BackgroundProperty, MenuHoverBgBrush, "ItemBorder"));
        itemTemplate.Triggers.Add(highlightTrigger);

        var disabledTrigger = new Trigger { Property = IsEnabledProperty, Value = false };
        disabledTrigger.Setters.Add(new Setter(OpacityProperty, 0.4, "ItemBorder"));
        disabledTrigger.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Arrow));
        itemTemplate.Triggers.Add(disabledTrigger);

        itemStyle.Setters.Add(new Setter(TemplateProperty, itemTemplate));
        contextStyle.Resources[typeof(MenuItem)] = itemStyle;

        return contextStyle;
    }

    // -------------------------------------------------------------------------
    // 状態更新・アニメーション
    // -------------------------------------------------------------------------
    private void Slot_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(RefreshVisuals));
            return;
        }

        RefreshVisuals();
    }

    private void RefreshAllSlotVisuals()
    {
        var smartLabels = WindowSlot.BuildMicroSmartLabels(_slots);

        for (var i = 0; i < 4 && i < _slots.Count; i++)
        {
            var slot = _slots[i];
            var sector = _sectors[i];
            sector.SlotKeyText.Text = slot.Name;

            var smart = smartLabels.TryGetValue(slot.Name, out var found) ? found : string.Empty;
            sector.ShortLabelText.Text = smart;
            sector.ShortLabelText.Visibility = string.IsNullOrEmpty(smart) ? Visibility.Collapsed : Visibility.Visible;

            ApplySectorVisual(i);
        }

        UpdateCoreWorkingOrbit();

        if (_infoPillPopup.IsOpen)
        {
            UpdateInfoPill(_hoveredSectorIndex ?? _selectedSectorIndex);
        }
    }

    // どれか 1 スロットでも AI 実行中のときだけ、本体円の外周 LED 周回を動かす（全スロットが実行中でなければ止めて消す）。
    // 控え入替待ちの間は、扇の内側ホタル点滅と同じく配置先表示を優先して止める。
    private void UpdateCoreWorkingOrbit()
    {
        var anyWorking = false;
        if (_pendingStoredPanel is null)
        {
            for (var i = 0; i < 4 && i < _slots.Count; i++)
            {
                if (_slots[i].AiActivity == AiActivityState.Working)
                {
                    anyWorking = true;
                    break;
                }
            }
        }

        _coreWorkingOrbitGlow.Activity = anyWorking ? AiActivityState.Working : AiActivityState.Idle;
    }

    private void ApplySectorVisual(int index)
    {
        if (index < 0 || index >= _slots.Count)
        {
            return;
        }

        var slot = _slots[index];
        var sector = _sectors[index];
        var isHovered = _hoveredSectorIndex == index;
        var isWheelSelected = _isExpanded && _wheelSelectActive && _selectedSectorIndex == index;
        var isAssignWaiting = _pendingStoredPanel is not null;
        var isWorking = slot.AiActivity == AiActivityState.Working;
        var isDone = slot.AiActivity == AiActivityState.Done;
        var isMissing = slot.WindowStatus == SlotWindowStatus.Missing;
        var isLaunching = slot.WindowStatus == SlotWindowStatus.Launching;
        var isHidden = slot.IsHidden && !isMissing;

        // 未起動スロットはクリック起動しないため、控え入替待ち以外のときは通常矢印カーソルにする。
        sector.BasePath.Cursor = (isAssignWaiting || !isMissing) ? Cursors.Hand : Cursors.Arrow;

        // ステータス LED ドット（緑＝起動済み、黄＝起動中／非表示、ローズ赤＝停止中、青＝AI完了）。
        // アプリ種別（VS Code / Antigravity / CLI）はホバー時のライブ情報ピルへ明示する。
        sector.StatusDot.Fill = isDone
            ? StatusAiDoneDotBrush
            : isHidden
                ? StatusHiddenDotBrush
                : isLaunching
                    ? StatusLaunchingDotBrush
                    : isMissing
                        ? StatusMissingDotBrush
                        : StatusReadyDotBrush;

        // AI 作業中: 中央の「非」リング側の内側円弧だけを 4.8 秒周期でゆっくりホタル点滅させる。
        if (isWorking && !isAssignWaiting)
        {
            sector.InnerWorkingArcLayer.Visibility = Visibility.Visible;
            if (!sector.IsAiWorkingAnimating)
            {
                sector.IsAiWorkingAnimating = true;
                var workingFirefly = CreateAiWorkingFireflyAnimation();
                sector.InnerWorkingArcLayer.BeginAnimation(OpacityProperty, workingFirefly);
            }
        }
        else
        {
            if (sector.IsAiWorkingAnimating)
            {
                sector.IsAiWorkingAnimating = false;
                sector.InnerWorkingArcLayer.BeginAnimation(OpacityProperty, null);
            }

            sector.InnerWorkingArcLayer.Opacity = 0.0;
            sector.InnerWorkingArcLayer.Visibility = Visibility.Collapsed;
        }

        // 扇の地色・枠線:
        // 1 面フォーカス中は太い鮮やか枠（2.0px）で主役を示し、
        // マウスホバー／ホイール選択中は細い控えめ枠（0.9px）＋穏やかな地の持ち上げに抑えて誤認を防ぐ。
        if (sector.IsDropHighlighted || isAssignWaiting)
        {
            sector.BasePath.Fill = isHovered || sector.IsDropHighlighted ? SectorHoverFillBrush : SectorAssignFillBrush;
            sector.BasePath.Stroke = SectorAssignStrokeBrush;
            sector.BasePath.StrokeThickness = isHovered || sector.IsDropHighlighted ? 2.0 : 1.5;
        }
        else if (isDone)
        {
            sector.BasePath.Fill = AiDoneBaseFillBrush;
            sector.BasePath.Stroke = AiDoneStrokeBrush;
            sector.BasePath.StrokeThickness = 1.6;
        }
        else if (slot.IsFocused)
        {
            sector.BasePath.Fill = SectorFocusedFillBrush;
            sector.BasePath.Stroke = slot.DisplayBrush ?? CoreActiveBorderBrush;
            sector.BasePath.StrokeThickness = 2.0;
        }
        else if (isHovered || isWheelSelected)
        {
            sector.BasePath.Fill = isMissing ? SectorMissingHoverFillBrush : SectorHoverFillBrush;
            sector.BasePath.Stroke = SectorHoverStrokeBrush;
            sector.BasePath.StrokeThickness = 0.9;
        }
        else if (isHidden)
        {
            sector.BasePath.Fill = SectorHiddenFillBrush;
            sector.BasePath.Stroke = SectorHiddenStrokeBrush;
            sector.BasePath.StrokeThickness = 1.1;
        }
        else if (isLaunching)
        {
            sector.BasePath.Fill = SectorLaunchingFillBrush;
            sector.BasePath.Stroke = SectorLaunchingStrokeBrush;
            sector.BasePath.StrokeThickness = 1.15;
        }
        else if (isMissing)
        {
            sector.BasePath.Fill = SectorMissingFillBrush;
            sector.BasePath.Stroke = SectorMissingStrokeBrush;
            sector.BasePath.StrokeThickness = 1.0;
        }
        else
        {
            sector.BasePath.Fill = SectorReadyFillBrush;
            sector.BasePath.Stroke = SectorReadyStrokeBrush;
            sector.BasePath.StrokeThickness = 1.1;
        }

        // A〜D アルファベット＆短縮名の色（縮小表示の CompactSlotBadgeTextStyle と完全統一）:
        // 起動済み＝緑発光（#45D483）、フォーカス中＝高輝度緑、停止中＝非発光の暗いグレー（#6F7B74）、
        // 起動中＝黄（#F2CA57）、非表示＝黄土（#D4C47A）、AI完了＝白青（#F0F8FF）。
        if (isDone)
        {
            sector.SlotKeyText.Foreground = TextAiDoneBrush;
            sector.ShortLabelText.Foreground = TextAiDoneSubBrush;
        }
        else if (isHidden)
        {
            sector.SlotKeyText.Foreground = TextHiddenKeyBrush;
            sector.ShortLabelText.Foreground = TextHiddenSubBrush;
        }
        else if (isLaunching)
        {
            sector.SlotKeyText.Foreground = TextLaunchingKeyBrush;
            sector.ShortLabelText.Foreground = TextLaunchingSubBrush;
        }
        else if (isMissing)
        {
            sector.SlotKeyText.Foreground = TextMissingKeyBrush;
            sector.ShortLabelText.Foreground = TextMissingSubBrush;
        }
        else if (slot.IsFocused)
        {
            sector.SlotKeyText.Foreground = TextFocusedKeyBrush;
            sector.ShortLabelText.Foreground = TextPrimaryBrush;
        }
        else
        {
            sector.SlotKeyText.Foreground = AccentGreenBrush;
            sector.ShortLabelText.Foreground = TextReadySubBrush;
        }

        // AI 完了の青ホタル点滅（3.0 秒周期の滑らかな非対称カーブ）
        if (isDone && !isAssignWaiting)
        {
            if (!sector.IsAiDoneAnimating)
            {
                sector.IsAiDoneAnimating = true;
                var firefly = CreateAiDoneFireflyAnimation();
                sector.AiDoneWashPath.BeginAnimation(OpacityProperty, firefly);
            }
        }
        else if (sector.IsAiDoneAnimating)
        {
            sector.IsAiDoneAnimating = false;
            sector.AiDoneWashPath.BeginAnimation(OpacityProperty, null);
            sector.AiDoneWashPath.Opacity = 0.0;
        }
    }

    private void OnDialPointerEntered()
    {
        _leaveGraceTimer.Stop();
        _isHovered = true;
        SetExpandedState(true);
    }

    private void OnDialPointerLeft()
    {
        _isHovered = false;
        _hoveredSectorIndex = null;
        _wheelSelectActive = false;
        if (!ShouldRemainExpanded())
        {
            _leaveGraceTimer.Stop();
            _leaveGraceTimer.Start();
        }
        else
        {
            RefreshAllSlotVisuals();
        }
    }

    private void OnSectorPointerEntered(int index)
    {
        OnDialPointerEntered();
        _hoveredSectorIndex = index;
        _selectedSectorIndex = index;
        _wheelSelectActive = false;
        for (var i = 0; i < 4; i++)
        {
            ApplySectorVisual(i);
        }

        UpdateInfoPill(index);
    }

    private void OnSectorPointerLeft(int index)
    {
        if (_hoveredSectorIndex == index)
        {
            _hoveredSectorIndex = null;
            ApplySectorVisual(index);
        }
    }

    private void OnDialMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        OnDialPointerEntered();
        _wheelSelectActive = true;
        StepWheelSelection(e.Delta < 0 ? +1 : -1);
        UpdateInfoPill(_selectedSectorIndex);
    }

    private void StepWheelSelection(int delta)
    {
        if (_slots.Count == 0)
        {
            return;
        }

        _selectedSectorIndex = (_selectedSectorIndex + delta + _slots.Count) % _slots.Count;
        for (var i = 0; i < 4; i++)
        {
            ApplySectorVisual(i);
        }
    }

    private bool ShouldRemainExpanded()
    {
        return _isStoredOverviewOpen || _isContextMenuOpen || _auxApplicationsPopup.IsOpen || _pendingStoredPanel is not null || _isDraggingWindow;
    }

    private void SetExpandedState(bool expanded, bool animate = true)
    {
        if (_isExpanded == expanded && animate)
        {
            UpdateIdleOrActiveOpacity(animate: true);
            return;
        }

        _isExpanded = expanded;
        _coreOuterRing.Stroke = expanded ? CoreActiveBorderBrush : CoreIdleBorderBrush;
        _satelliteCanvas.IsHitTestVisible = expanded;

        if (!expanded)
        {
            HideInfoPill();
        }

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            _satelliteCanvas.BeginAnimation(OpacityProperty, null);
            _satelliteScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _satelliteScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _satelliteCanvas.Opacity = expanded ? 1.0 : 0.0;
            _satelliteScale.ScaleX = expanded ? 1.0 : 0.86;
            _satelliteScale.ScaleY = expanded ? 1.0 : 0.86;
            UpdateIdleOrActiveOpacity(animate: false);
            for (var i = 0; i < 4; i++)
            {
                ApplySectorVisual(i);
            }

            return;
        }

        var duration = new Duration(expanded ? ExpandAnimDuration : CollapseAnimDuration);
        IEasingFunction ease = expanded
            ? new CubicEase { EasingMode = EasingMode.EaseOut }
            : new SineEase { EasingMode = EasingMode.EaseInOut };

        _satelliteCanvas.BeginAnimation(OpacityProperty, new DoubleAnimation(expanded ? 1.0 : 0.0, duration) { EasingFunction = ease });
        _satelliteScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(expanded ? 1.0 : 0.86, duration) { EasingFunction = ease });
        _satelliteScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(expanded ? 1.0 : 0.86, duration) { EasingFunction = ease });

        UpdateIdleOrActiveOpacity(animate: true);
        for (var i = 0; i < 4; i++)
        {
            ApplySectorVisual(i);
        }
    }

    private double ComputeIdleOpacity(bool forceNoAiAlert = false, bool forceAiAlert = false)
    {
        if (!_ghostOpacityEnabled)
        {
            return 1.0;
        }

        var idleOpacity = _ghostOpacityPercent / 100.0;
        // マウスオーバーが外れている間は、1 面フォーカス中や AI 実行中でも薄く表示（設定透過率）し、
        // AI 応答完了（Done）を表示するときだけ濃くブーストして気づきやすくする。
        var hasAiDoneAlert = forceAiAlert || (!forceNoAiAlert && _slots.Any(slot =>
            slot.AiActivity == AiActivityState.Done));
        return hasAiDoneAlert ? Math.Max(idleOpacity, IdleAiAlertOpacity) : idleOpacity;
    }

    private void UpdateIdleOrActiveOpacity(bool animate)
    {
        var targetOpacity = _isExpanded || ShouldRemainExpanded() ? 1.0 : ComputeIdleOpacity();
        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            _dialRoot.BeginAnimation(OpacityProperty, null);
            _dialRoot.Opacity = targetOpacity;
            return;
        }

        var anim = new DoubleAnimation(targetOpacity, new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        _dialRoot.BeginAnimation(OpacityProperty, anim);
    }

    private void UpdateInfoPill(int index)
    {
        if (_closed || !IsVisible || index < 0 || index >= _slots.Count)
        {
            return;
        }

        ApplyInfoPillContent(index);

        if (!_infoPillPopup.IsOpen)
        {
            _infoPillPopup.IsOpen = true;
        }
        else
        {
            var prev = _infoPillPopup.HorizontalOffset;
            _infoPillPopup.HorizontalOffset = prev + 0.01;
            _infoPillPopup.HorizontalOffset = prev;
        }
    }

    private void ApplyInfoPillContent(int index)
    {
        if (index < 0 || index >= _slots.Count)
        {
            return;
        }

        var slot = _slots[index];
        _infoPillKeyText.Text = slot.Name;
        _infoPillTitleText.Text = slot.DisplayTitle;
        _infoPillAppDot.Fill = GetApplicationDotBrush(slot);
        _infoPillAppText.Text = slot.ApplicationBadgeText;

        // 左上 [A]〜[D] バッジの色も縮小表示（CompactSlotBadgeStyle）と統一
        if (slot.AiActivity == AiActivityState.Done)
        {
            _infoPillKeyBadge.Background = PillAiDoneBgBrush;
            _infoPillKeyBadge.BorderBrush = PillAiDoneBorderBrush;
            _infoPillKeyText.Foreground = TextAiDoneBrush;
        }
        else if (slot.WindowStatus == SlotWindowStatus.Missing)
        {
            _infoPillKeyBadge.Background = KeyBadgeMissingBgBrush;
            _infoPillKeyBadge.BorderBrush = KeyBadgeMissingBorderBrush;
            _infoPillKeyText.Foreground = TextMissingKeyBrush;
        }
        else if (slot.WindowStatus == SlotWindowStatus.Launching)
        {
            _infoPillKeyBadge.Background = PillLaunchingBgBrush;
            _infoPillKeyBadge.BorderBrush = PillLaunchingBorderBrush;
            _infoPillKeyText.Foreground = PillLaunchingFgBrush;
        }
        else if (slot.IsHidden)
        {
            _infoPillKeyBadge.Background = PillHiddenBgBrush;
            _infoPillKeyBadge.BorderBrush = PillHiddenBorderBrush;
            _infoPillKeyText.Foreground = PillHiddenFgBrush;
        }
        else
        {
            _infoPillKeyBadge.Background = CenterNormalBgBrush;
            _infoPillKeyBadge.BorderBrush = SatBorderBrush;
            _infoPillKeyText.Foreground = AccentGreenBrush;
        }

        // 右下の状態バッジ（標準表示の WindowPill／縮小表示の起・停ボタンと同じ色体系）
        if (slot.AiActivity == AiActivityState.Working)
        {
            _infoPillStatusText.Text = string.IsNullOrEmpty(slot.AiElapsedText)
                ? "● AI 作業中"
                : $"● 作業中 {slot.AiElapsedText}";
            _infoPillStatusText.Foreground = PillFocusedFgBrush;
            _infoPillStatusBadge.Background = PillFocusedBgBrush;
            _infoPillStatusBadge.BorderBrush = CoreActiveBorderBrush;
        }
        else if (slot.AiActivity == AiActivityState.Done)
        {
            _infoPillStatusText.Text = string.IsNullOrEmpty(slot.AiElapsedText)
                ? "● AI 応答完了"
                : $"● 完了 ({slot.AiElapsedText})";
            _infoPillStatusText.Foreground = PillAiDoneFgBrush;
            _infoPillStatusBadge.Background = PillAiDoneBgBrush;
            _infoPillStatusBadge.BorderBrush = PillAiDoneBorderBrush;
        }
        else if (slot.IsFocused)
        {
            _infoPillStatusText.Text = "1 面フォーカス中";
            _infoPillStatusText.Foreground = PillFocusedFgBrush;
            _infoPillStatusBadge.Background = PillFocusedBgBrush;
            _infoPillStatusBadge.BorderBrush = CoreActiveBorderBrush;
        }
        else if (slot.IsHidden && slot.WindowStatus != SlotWindowStatus.Missing)
        {
            _infoPillStatusText.Text = "非表示";
            _infoPillStatusText.Foreground = PillHiddenFgBrush;
            _infoPillStatusBadge.Background = PillHiddenBgBrush;
            _infoPillStatusBadge.BorderBrush = PillHiddenBorderBrush;
        }
        else if (slot.WindowStatus == SlotWindowStatus.Launching)
        {
            _infoPillStatusText.Text = slot.WindowStatusText;
            _infoPillStatusText.Foreground = PillLaunchingFgBrush;
            _infoPillStatusBadge.Background = PillLaunchingBgBrush;
            _infoPillStatusBadge.BorderBrush = PillLaunchingBorderBrush;
        }
        else if (slot.WindowStatus == SlotWindowStatus.Missing)
        {
            _infoPillStatusText.Text = slot.WindowStatusText;
            _infoPillStatusText.Foreground = PillMissingFgBrush;
            _infoPillStatusBadge.Background = PillMissingBgBrush;
            _infoPillStatusBadge.BorderBrush = PillMissingBorderBrush;
        }
        else
        {
            _infoPillStatusText.Text = slot.WindowStatusText;
            _infoPillStatusText.Foreground = PillReadyFgBrush;
            _infoPillStatusBadge.Background = PillReadyBgBrush;
            _infoPillStatusBadge.BorderBrush = PillReadyBorderBrush;
        }

        if (_pendingStoredPanel is { } pending)
        {
            _infoPillAssignHint.Text = $"▶ クリックで「{pending.DisplayTitle}」を {slot.Name} に表示";
            _infoPillAssignHint.Visibility = Visibility.Visible;
        }
        else
        {
            _infoPillAssignHint.Visibility = Visibility.Collapsed;
        }
    }

    private void HideInfoPill()
    {
        if (_infoPillPopup.IsOpen)
        {
            _infoPillPopup.IsOpen = false;
        }
    }

    // -------------------------------------------------------------------------
    // クリック・ドラッグ移動・中クリック・ドラッグ＆ドロップ
    // -------------------------------------------------------------------------
    private void CoreHost_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var onCenterButton = IsDescendantOf(e.OriginalSource as DependencyObject, _centerVisibilityButton);
        _pointerDownOnCenterButton = onCenterButton;
        _suppressNextCenterButtonClick = false;
        if (onCenterButton)
        {
            _pointerDownSectorIndex = null;
        }

        // 扇上だけでなく中央の「非 / 表」丸ボタン上からのドラッグでもウィンドウ移動できるようにする。
        // 移動量が DragStartThresholdDip 未満のまま離したときは通常のクリック（表示/非表示切替）として動作する。
        _isPointerDown = true;
        _isDraggingWindow = false;
        _dragStartScreenPx = PointToScreen(e.GetPosition(this));
        _dragStartWindowLeft = Left;
        _dragStartWindowTop = Top;
    }

    private void CoreHost_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPointerDown)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _isPointerDown = false;
            _isDraggingWindow = false;
            _pointerDownOnCenterButton = false;
            _pointerDownSectorIndex = null;
            if (_coreHost.IsMouseCaptured)
            {
                _coreHost.ReleaseMouseCapture();
            }

            return;
        }

        var currentScreenPx = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        var dxDip = (currentScreenPx.X - _dragStartScreenPx.X) / dpi.DpiScaleX;
        var dyDip = (currentScreenPx.Y - _dragStartScreenPx.Y) / dpi.DpiScaleY;

        if (!_isDraggingWindow)
        {
            if (Math.Sqrt((dxDip * dxDip) + (dyDip * dyDip)) < DragStartThresholdDip)
            {
                return;
            }

            _isDraggingWindow = true;
            _pointerDownSectorIndex = null;
            if (_pointerDownOnCenterButton)
            {
                _suppressNextCenterButtonClick = true;
            }

            CloseAuxApplicationsPopup();
            HideInfoPill();
            _coreHost.CaptureMouse();
        }

        Left = _dragStartWindowLeft + dxDip;
        Top = _dragStartWindowTop + dyDip;
        NotifyCoreCenterMoved();
    }

    private void CoreHost_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPointerDown)
        {
            return;
        }

        var wasDragging = _isDraggingWindow;
        var clickedSector = _pointerDownSectorIndex;
        var wasCenterButton = _pointerDownOnCenterButton;
        _isPointerDown = false;
        _isDraggingWindow = false;
        _pointerDownOnCenterButton = false;
        _pointerDownSectorIndex = null;

        if (_coreHost.IsMouseCaptured)
        {
            _coreHost.ReleaseMouseCapture();
        }

        if (wasDragging)
        {
            NotifyCoreCenterMoved();
            if (wasCenterButton)
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => _suppressNextCenterButtonClick = false));
            }

            e.Handled = true;
            return;
        }

        if (wasCenterButton)
        {
            // ドラッグ閾値未満の単クリックは _centerVisibilityButton.Click に任せる。
            return;
        }

        if (clickedSector is { } index && index >= 0 && index < _slots.Count)
        {
            _selectedSectorIndex = index;
            if (_pendingStoredPanel is { } pending)
            {
                _onStoredPanelDropped(pending, _slots[index]);
            }
            else if (_slots[index].WindowStatus != SlotWindowStatus.Missing)
            {
                // 起動済みスロットのみクリックでフォーカス切替する（未起動スロットは右クリック「起動する」から明示起動）。
                _onSlotActivated(_slots[index]);
            }

            e.Handled = true;
        }
    }

    private void CoreHost_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // 中クリックでホイール選択中の起動済みスロットを即座にフォーカス切替（未起動スロットは誤起動させない）
        if (e.ChangedButton == MouseButton.Middle
            && _selectedSectorIndex >= 0
            && _selectedSectorIndex < _slots.Count
            && _slots[_selectedSectorIndex].WindowStatus != SlotWindowStatus.Missing)
        {
            _onSlotActivated(_slots[_selectedSectorIndex]);
            e.Handled = true;
        }
    }

    private void HandleSectorDragOver(int index, DragEventArgs e)
    {
        OnDialPointerEntered();
        var canDrop = e.Data.GetData("StoredPanelSlot") is StoredPanelSlot { HasContent: true }
            || (e.Data.GetData("WindowSlot") is WindowSlot src && index < _slots.Count && !ReferenceEquals(src, _slots[index]));
        e.Effects = canDrop ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;

        _sectors[index].IsDropHighlighted = canDrop;
        ApplySectorVisual(index);
        if (canDrop)
        {
            UpdateInfoPill(index);
        }
    }

    private void HandleSectorDrop(int index, DragEventArgs e)
    {
        _sectors[index].IsDropHighlighted = false;
        ApplySectorVisual(index);
        e.Handled = true;

        if (index < 0 || index >= _slots.Count)
        {
            return;
        }

        if (e.Data.GetData("StoredPanelSlot") is StoredPanelSlot { HasContent: true } stored)
        {
            _onStoredPanelDropped(stored, _slots[index]);
            return;
        }

        if (e.Data.GetData("WindowSlot") is WindowSlot source && !ReferenceEquals(source, _slots[index]))
        {
            _onSlotSwapped(source, _slots[index]);
        }
    }

    private void NotifyCoreCenterMoved()
    {
        var coreRect = GetCoreScreenRect();
        _onCoreCenterMoved(new Point(coreRect.Left + (coreRect.Width / 2.0), coreRect.Top + (coreRect.Height / 2.0)));
    }

    private void ApplyFloatingWindowStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // AllowsTransparency=True で付与済みの WS_EX_LAYERED を保ったまま、
        // タスクバー非表示（WS_EX_TOOLWINDOW）とフォーカス奪取抑止（WS_EX_NOACTIVATE）だけを追加する。
        var exStyle = GetWindowLong(handle, GwlExStyle);
        _ = SetWindowLong(handle, GwlExStyle, exStyle | WsExToolWindow | WsExNoActivate);
    }

    private void OnWindowClosedCleanup()
    {
        _closed = true;
        _leaveGraceTimer.Stop();
        CloseActiveContextMenu();
        CloseAuxApplicationsPopup();
        HideInfoPill();
        foreach (var slot in _slots)
        {
            slot.PropertyChanged -= Slot_PropertyChanged;
        }
    }

    // -------------------------------------------------------------------------
    // ジオメトリ・ヘルパー
    // -------------------------------------------------------------------------
    private static Brush GetApplicationDotBrush(WindowSlot slot)
    {
        if (slot.IsVsCodeApplication)
        {
            return VsCodeDotBrush;
        }

        if (slot.IsAntigravityApplication)
        {
            return AntigravityDotBrush;
        }

        return CliDotBrush;
    }

    private static PathGeometry CreateSectorGeometry(double cx, double cy, double outerR, double innerR, double startDeg, double endDeg)
    {
        var rad1 = startDeg * Math.PI / 180.0;
        var rad2 = endDeg * Math.PI / 180.0;

        var p1 = new Point(cx + (outerR * Math.Cos(rad1)), cy + (outerR * Math.Sin(rad1)));
        var p2 = new Point(cx + (outerR * Math.Cos(rad2)), cy + (outerR * Math.Sin(rad2)));
        var p3 = new Point(cx + (innerR * Math.Cos(rad2)), cy + (innerR * Math.Sin(rad2)));
        var p4 = new Point(cx + (innerR * Math.Cos(rad1)), cy + (innerR * Math.Sin(rad1)));

        var figure = new PathFigure { StartPoint = p1, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment(p2, new Size(outerR, outerR), 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true));
        figure.Segments.Add(new LineSegment(p3, isStroked: true));
        figure.Segments.Add(new ArcSegment(p4, new Size(innerR, innerR), 0, isLargeArc: false, SweepDirection.Counterclockwise, isStroked: true));

        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static PathGeometry CreateArcGeometry(double cx, double cy, double radius, double startDeg, double endDeg)
    {
        var rad1 = startDeg * Math.PI / 180.0;
        var rad2 = endDeg * Math.PI / 180.0;
        var p1 = new Point(cx + (radius * Math.Cos(rad1)), cy + (radius * Math.Sin(rad1)));
        var p2 = new Point(cx + (radius * Math.Cos(rad2)), cy + (radius * Math.Sin(rad2)));

        var figure = new PathFigure { StartPoint = p1, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(p2, new Size(radius, radius), 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true));

        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static DoubleAnimationUsingKeyFrames CreateAiWorkingFireflyAnimation()
    {
        var rise = new KeySpline(0.38, 0.0, 0.28, 1.0);
        rise.Freeze();
        var fall = new KeySpline(0.42, 0.0, 0.58, 1.0);
        fall.Freeze();

        var anim = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.26, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(1.00, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2.1)), rise));
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(0.26, KeyTime.FromTimeSpan(AiWorkingFireflyDuration), fall));
        return anim;
    }

    private static DoubleAnimationUsingKeyFrames CreateAiDoneFireflyAnimation()
    {
        var rise = new KeySpline(0.32, 0.0, 0.30, 1.0);
        rise.Freeze();
        var fall = new KeySpline(0.42, 0.0, 0.58, 1.0);
        fall.Freeze();

        var anim = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0.06, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(0.88, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.15)), rise));
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(0.06, KeyTime.FromTimeSpan(AiDoneCycleDuration), fall));
        return anim;
    }

    private static ControlTemplate CreatePlainButtonTemplate()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        return new ControlTemplate(typeof(Button))
        {
            VisualTree = presenter
        };
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class SectorVisual(
        Canvas root,
        Path basePath,
        Path aiDoneWashPath,
        Ellipse statusDot,
        TextBlock slotKeyText,
        TextBlock shortLabelText,
        StackPanel topLine,
        Grid labelBox,
        Canvas innerWorkingArcLayer)
    {
        public Canvas Root { get; } = root;
        public Path BasePath { get; } = basePath;
        public Path AiDoneWashPath { get; } = aiDoneWashPath;
        public Ellipse StatusDot { get; } = statusDot;
        public TextBlock SlotKeyText { get; } = slotKeyText;
        public TextBlock ShortLabelText { get; } = shortLabelText;
        public StackPanel TopLine { get; } = topLine;
        public Grid LabelBox { get; } = labelBox;
        public Canvas InnerWorkingArcLayer { get; } = innerWorkingArcLayer;
        public bool IsAiWorkingAnimating { get; set; }
        public bool IsAiDoneAnimating { get; set; }
        public bool IsDropHighlighted { get; set; }
    }

    private delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public NativePoint Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdllHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
