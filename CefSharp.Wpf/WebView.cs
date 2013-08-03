﻿// Copyright © 2010-2013 The CefSharp Project. All rights reserved.
//
// Use of this source code is governed by a BSD-style license that can be found in the LICENSE file.

using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CefSharp.Wpf
{
    public class WebView : ContentControl, IRenderWebBrowser, IWpfWebBrowser
    {
        private HwndSource source;
        private HwndSourceHook sourceHook;
        private BrowserCore browserCore;
        private DispatcherTimer tooltipTimer;
        private readonly ToolTip toolTip;
        private CefBrowserWrapper cefBrowserWrapper;
        private bool isOffscreenBrowserCreated;
        private bool ignoreUriChange;

        private Image image;
        private InteropBitmap interopBitmap;
        private InteropBitmap popupInteropBitmap;

        public IJsDialogHandler JsDialogHandler { get; set; }
        public IKeyboardHandler KeyboardHandler { get; set; }
        public IRequestHandler RequestHandler { get; set; }
        public ILifeSpanHandler LifeSpanHandler { get; set; }
        public bool IsBrowserInitialized { get; private set; }
        public event ConsoleMessageEventHandler ConsoleMessage;
        public event PropertyChangedEventHandler PropertyChanged;
        public event LoadCompletedEventHandler LoadCompleted;
        public event LoadErrorEventHandler LoadError;

        public IntPtr FileMappingHandle { get; set; }
        public IntPtr PopupFileMappingHandle { get; set; }

        public ICommand BackCommand { get; private set; }
        public ICommand ForwardCommand { get; private set; }

        public int BytesPerPixel
        {
            get { return PixelFormats.Bgr32.BitsPerPixel / 8; }
        }

        int IRenderWebBrowser.Width
        {
            get { return (int) ActualWidth; }
        }

        int IRenderWebBrowser.Height
        {
            get { return (int) ActualHeight; }
        }

        #region Address dependency property

        public string Address
        {
            get { return (string) GetValue(AddressProperty); }
            set { SetValue(AddressProperty, value); }
        }

        public static readonly DependencyProperty AddressProperty =
            DependencyProperty.Register("Address", typeof(string), typeof(WebView),
                                        new UIPropertyMetadata(null, (sender, e) => ((WebView) sender).OnAddressChanged()));

        private void OnAddressChanged()
        {
            if (ignoreUriChange)
            {
                return;
            }

            if (!Cef.IsInitialized &&
                !Cef.Initialize())
            {
                throw new InvalidOperationException("Cef::Initialize() failed");
            }

            if (browserCore == null)
            {
                browserCore = new BrowserCore(Address);
                browserCore.PropertyChanged += OnBrowserCorePropertyChanged;

                // TODO: Consider making the delay here configurable.
                tooltipTimer = new DispatcherTimer(
                    TimeSpan.FromSeconds(0.5),
                    DispatcherPriority.Render,
                    OnTooltipTimerTick,
                    Dispatcher
                );
            }

            browserCore.Address = browserCore.Address;

            if (isOffscreenBrowserCreated)
            {
                cefBrowserWrapper.LoadUrl(Address);
            }
            else
            {
                if (source != null)
                {
                    CreateOffscreenBrowser();
                }
            }
        }

        #endregion Address dependency property

        #region IsLoading dependency property

        public bool IsLoading
        {
            get { return (bool) GetValue(IsLoadingProperty); }
            set { SetValue(IsLoadingProperty, value); }
        }

        public static readonly DependencyProperty IsLoadingProperty =
            DependencyProperty.Register("IsLoading", typeof(bool), typeof(WebView), new PropertyMetadata(false));

        #endregion IsLoading dependency property

        #region Title dependency property

        public string Title
        {
            get { return (string) GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register("Title", typeof(string), typeof(WebView), new PropertyMetadata(defaultValue: null));

        #endregion Title dependency property

        #region TooltipText dependency property

        public string TooltipText
        {
            get { return (string)GetValue(TooltipTextProperty); }
            set { SetValue(TooltipTextProperty, value); }
        }

        public static readonly DependencyProperty TooltipTextProperty = 
            DependencyProperty.Register("TooltipText", typeof(string), typeof(WebView), new PropertyMetadata(null, (sender, e) => ((WebView) sender).OnTooltipTextChanged()));

        private void OnTooltipTextChanged()
        {
            tooltipTimer.Stop();

            if (String.IsNullOrEmpty(TooltipText))
            {
                Dispatcher.BeginInvoke((Action) (() => UpdateTooltip(null)), DispatcherPriority.Render);
            }
            else
            {
                tooltipTimer.Start();
            }
        }

        #endregion

        #region WebBrowser dependency property

        public IWebBrowser WebBrowser
        {
            get { return (IWebBrowser) GetValue(WebBrowserProperty); }
            set { SetValue(WebBrowserProperty, value); }
        }

        public static readonly DependencyProperty WebBrowserProperty =
            DependencyProperty.Register("WebBrowser", typeof(IWebBrowser), typeof(WebView), new UIPropertyMetadata(defaultValue: null));

        #endregion WebBrowser dependency property

        public WebView()
        {
            Focusable = true;
            FocusVisualStyle = null;
            IsTabStop = true;

            Dispatcher.BeginInvoke((Action) (() => WebBrowser = this));

            //_scriptCore = new ScriptCore();
            //_paintPopupDelegate = gcnew ActionHandler(this, &WebView::SetPopupBitmap);
            //_resizePopupDelegate = gcnew ActionHandler(this, &WebView::SetPopupSizeAndPositionImpl);

            Unloaded += OnUnloaded;

            ToolTip = toolTip = new ToolTip();
            toolTip.StaysOpen = true;
            toolTip.Visibility = Visibility.Collapsed;
            toolTip.Closed += OnTooltipClosed;

            Application.Current.Exit += OnApplicationExit;

            BackCommand = new DelegateCommand(Back, CanGoBack);
            ForwardCommand = new DelegateCommand(Forward, CanGoForward);
        }

        private void OnApplicationExit(object sender, ExitEventArgs e)
        {
            ShutdownCefBrowserWrapper();
        }

        public void OnUnloaded(object sender, RoutedEventArgs routedEventArgs)
        {
            // TODO: Will this really work correctly in a TabControl-based approach? (where we might get Loaded and Unloaded
            // multiple times)
            RemoveSourceHook();
            ShutdownCefBrowserWrapper();
        }

        private void ShutdownCefBrowserWrapper()
        {
            if (cefBrowserWrapper == null)
            {
                return;
            }

            cefBrowserWrapper.Close();
            cefBrowserWrapper.Dispose();
            cefBrowserWrapper = null;
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            cefBrowserWrapper = new CefBrowserWrapper(this);

            AddSourceHook();

            if (Address != null)
            {
                CreateOffscreenBrowser();
            }

            Content = image = new Image();
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

            //_popup = gcnew Popup();
            //_popup->Child = _popupImage = gcnew Image();

            //_popup->MouseDown += gcnew MouseButtonEventHandler(this, &WebView::OnPopupMouseDown);
            //_popup->MouseUp += gcnew MouseButtonEventHandler(this, &WebView::OnPopupMouseUp);
            //_popup->MouseMove += gcnew MouseEventHandler(this, &WebView::OnPopupMouseMove);
            //_popup->MouseLeave += gcnew MouseEventHandler(this, &WebView::OnPopupMouseLeave);
            //_popup->MouseWheel += gcnew MouseWheelEventHandler(this, &WebView::OnPopupMouseWheel);

            //_popup->PlacementTarget = this;
            //_popup->Placement = PlacementMode::Relative;

            image.Stretch = Stretch.None;
            image.HorizontalAlignment = HorizontalAlignment.Left;
            image.VerticalAlignment = VerticalAlignment.Top;

            //_popupImage->Stretch = Stretch::None;
            //_popupImage->HorizontalAlignment = ::HorizontalAlignment::Left;
            //_popupImage->VerticalAlignment = ::VerticalAlignment::Top;
        }

        private void CreateOffscreenBrowser()
        {
            // TODO: Make it possible to override the BrowserSettings using a dependency property.
            cefBrowserWrapper.CreateOffscreenBrowser(new BrowserSettings(), source.Handle, Address);
            isOffscreenBrowserCreated = true;
        }

        private void AddSourceHook()
        {
            if (source != null)
            {
                return;
            }

            source = (HwndSource) PresentationSource.FromVisual(this);

            if (source != null)
            {
                sourceHook = SourceHook;
                source.AddHook(sourceHook);
            }
        }

        private void RemoveSourceHook()
        {
            if (source != null &&
                sourceHook != null)
            {
                source.RemoveHook(sourceHook);
            }
        }

        private IntPtr SourceHook(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            handled = false;

            switch ((WM) message)
            {
                case WM.SYSCHAR:
                case WM.SYSKEYDOWN:
                case WM.SYSKEYUP:
                case WM.KEYDOWN:
                case WM.KEYUP:
                case WM.CHAR:
                    if (!IsFocused)
                    {
                        break;
                    }

                    if (message == (int) WM.SYSKEYDOWN &&
                        wParam.ToInt32() == KeyInterop.VirtualKeyFromKey(Key.F4))
                    {
                        // We don't want CEF to receive this event (and mark it as handled), since that makes it impossible to
                        // shut down a CefSharp-based app by pressing Alt-F4, which is kind of bad.
                        return IntPtr.Zero;
                    }

                    if (cefBrowserWrapper.SendKeyEvent(message, wParam.ToInt32()))
                    {
                        handled = true;
                    }

                    break;
            }

            return IntPtr.Zero;
        }

        protected override Size ArrangeOverride(Size arrangeBounds)
        {
            var size = base.ArrangeOverride(arrangeBounds);
            var newWidth = size.Width;
            var newHeight = size.Height;

            if (newWidth > 0 &&
                newHeight > 0)
            {
                cefBrowserWrapper.WasResized();
            }

            return size;
        }

        public void InvokeRenderAsync(Action callback)
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Render, callback);
            }
        }

        public void SetAddress(string address)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke((Action) (() => SetAddress(address)));
                return;
            }

            ignoreUriChange = true;
            Address = address;
            ignoreUriChange = false;

            // The tooltip should obviously also be reset (and hidden) when the address changes.
            TooltipText = null;
        }

        public void SetIsLoading(bool isLoading)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke((Action) (() => SetIsLoading(isLoading)));
                return;
            }

            IsLoading = isLoading;
        }

        public void SetNavState(bool canGoBack, bool canGoForward)
        {
            browserCore.SetNavState(canGoBack, canGoForward);
            ((DelegateCommand) BackCommand).RaiseCanExecuteChanged();
            ((DelegateCommand) ForwardCommand).RaiseCanExecuteChanged();
        }

        public void SetTitle(string title)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke((Action) (() => SetTitle(title)));
                return;
            }

            Title = title;
        }

        public void SetTooltipText(string tooltipText)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke((Action) (() => SetTooltipText(tooltipText)));
                return;
            }

            TooltipText = tooltipText;
        }

        private void OnBrowserCorePropertyChanged(Object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "Address")
            {
                Address = browserCore.Address;
            }
        }

        private void OnTooltipTimerTick(object sender, EventArgs e)
        {
            tooltipTimer.Stop();

            UpdateTooltip(TooltipText);
        }

        private void OnTooltipClosed(object sender, RoutedEventArgs e)
        {
            toolTip.Visibility = Visibility.Collapsed;

            // Set Placement to something other than PlacementMode::Mouse, so that when we re-show the tooltip in
            // UpdateTooltip(), the tooltip will be repositioned to the new mouse point.
            toolTip.Placement = PlacementMode.Absolute;
        }

        private void UpdateTooltip(string text)
        {
            if (String.IsNullOrEmpty(text))
            {
                toolTip.IsOpen = false;
            }
            else
            {
                toolTip.Content = text;
                toolTip.Placement = PlacementMode.Mouse;
                toolTip.Visibility = Visibility.Visible;
                toolTip.IsOpen = true;
            }
        }

        protected override void OnGotFocus(RoutedEventArgs e)
        {
            cefBrowserWrapper.SendFocusEvent(true);

            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(RoutedEventArgs e)
        {
            if (cefBrowserWrapper != null)
            {
                cefBrowserWrapper.SendFocusEvent(false);
            }

            base.OnLostFocus(e);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            OnPreviewKey(e);
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            OnPreviewKey(e);
        }

        private void OnPreviewKey(KeyEventArgs e)
        {
            // For some reason, not all kinds of keypresses triggers the appropriate WM_ messages handled by our SourceHook, so
            // we have to handle these extra keys here. Hooking the Tab key like this makes the tab focusing in essence work like
            // KeyboardNavigation.TabNavigation="Cycle"; you will never be able to Tab out of the web browser control.

            if (e.Key == Key.Tab ||
                new[] { Key.Left, Key.Right, Key.Up, Key.Down }.Contains(e.Key))
            {
                var message = (int) (e.IsDown ? WM.KEYDOWN : WM.KEYUP);
                var virtualKey = KeyInterop.VirtualKeyFromKey(e.Key);
                cefBrowserWrapper.SendKeyEvent(message, virtualKey);
                e.Handled = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var point = e.GetPosition(this);
            cefBrowserWrapper.OnMouseMove((int) point.X, (int) point.Y, mouseLeave: false);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            var point = e.GetPosition(this);

            cefBrowserWrapper.OnMouseWheel(
                (int) point.X,
                (int) point.Y,
                deltaX: 0,
                deltaY: e.Delta
            );
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            Focus();
            OnMouseButton(e);
            Mouse.Capture(this);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            OnMouseButton(e);
            Mouse.Capture(null);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            cefBrowserWrapper.OnMouseMove(0, 0, mouseLeave: true);
        }

        private void OnMouseButton(MouseButtonEventArgs e)
        {
            MouseButtonType mouseButtonType;

            switch (e.ChangedButton)
            {
                case MouseButton.Left:
                    mouseButtonType = MouseButtonType.Left;
                    break;

                case MouseButton.Middle:
                    mouseButtonType = MouseButtonType.Middle;
                    break;

                case MouseButton.Right:
                    mouseButtonType = MouseButtonType.Right;
                    break;

                default:
                    return;
            }

            var mouseUp = (e.ButtonState == MouseButtonState.Released);

            var point = e.GetPosition(this);
            cefBrowserWrapper.OnMouseButton((int) point.X, (int) point.Y, mouseButtonType, mouseUp, e.ClickCount);
        }

        public void OnInitialized()
        {
            browserCore.OnInitialized();
        }

        public void LoadHtml(string html, string url)
        {
            cefBrowserWrapper.LoadHtml(html, url);
        }

        public void Stop()
        {
            throw new NotImplementedException();
        }

        private void Back()
        {
            cefBrowserWrapper.GoBack();
        }

        private bool CanGoBack()
        {
            return browserCore.CanGoBack;
        }

        private void Forward()
        {
            cefBrowserWrapper.GoForward();
        }

        private bool CanGoForward()
        {
            return browserCore.CanGoForward;
        }

        public void Reload(bool ignoreCache)
        {
            throw new NotImplementedException();
        }

        public void Reload()
        {
            throw new NotImplementedException();
        }

        public void ClearHistory()
        {
            throw new NotImplementedException();
        }

        public void ShowDevTools()
        {
            var devToolsUrl = cefBrowserWrapper.DevToolsUrl;
            throw new NotImplementedException();
        }

        public void CloseDevTools()
        {
            throw new NotImplementedException();
        }

        public void Undo()
        {
            throw new NotImplementedException();
        }

        public void Redo()
        {
            throw new NotImplementedException();
        }

        public void Cut()
        {
            throw new NotImplementedException();
        }

        public void Copy()
        {
            throw new NotImplementedException();
        }

        public void Paste()
        {
            throw new NotImplementedException();
        }

        public void Delete()
        {
            throw new NotImplementedException();
        }

        public void SelectAll()
        {
            throw new NotImplementedException();
        }

        public void Print()
        {
            throw new NotImplementedException();
        }

        public void OnFrameLoadStart(string url)
        {
            browserCore.OnFrameLoadStart();
        }

        public void OnFrameLoadEnd(string url)
        {
            browserCore.OnFrameLoadEnd();

            if (LoadCompleted != null)
            {
                LoadCompleted(this, new LoadCompletedEventArgs(url));
            }
        }

        public void OnTakeFocus(bool next)
        {
            throw new NotImplementedException();
        }

        public void OnConsoleMessage(string message, string source, int line)
        {
            if (ConsoleMessage != null)
            {
                ConsoleMessage(this, new ConsoleMessageEventArgs(message, source, line));
            }
        }

        public void OnLoadError(string url, CefErrorCode errorCode, string errorText)
        {
            if (LoadError != null)
            {
                LoadError(url, errorCode, errorText);
            }
        }

        public void RegisterJsObject(string name, object objectToBind)
        {
            throw new NotImplementedException();
        }

        public IDictionary<string, object> GetBoundObjects()
        {
            throw new NotImplementedException();
        }

        public void ExecuteScript(string script)
        {
            browserCore.CheckBrowserInitialization();

            // Not yet ported to CEF3/C#-based WebView.
            throw new NotImplementedException();
            //CefRefPtr<CefBrowser> browser;
            //if (TryGetCefBrowser(browser))
            //{
            //    _scriptCore->Execute(browser, toNative(script));
            //}
        }

        public object EvaluateScript(string script, TimeSpan? timeout = null)
        {
            if (timeout == null)
            {
                timeout = TimeSpan.MaxValue;
            }

            // Not supported at the moment. Can be done, but will not be able to support a return value here like we're supposed
            // to, because of CEF3:s asynchronous nature.
            throw new NotImplementedException();
            //_browserCore->CheckBrowserInitialization();

            //CefRefPtr<CefBrowser> browser;
            //if (TryGetCefBrowser(browser))
            //{
            //    return _scriptCore->Evaluate(browser, toNative(script), timeout.TotalMilliseconds);
            //}
            //else
            //{
            //    return nullptr;
            //}
        }

        public void SetCursor(IntPtr handle)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke((Action<IntPtr>) SetCursor, handle);
                return;
            }

            Cursor = CursorInteropHelper.Create(new SafeFileHandle(handle, ownsHandle: false));
        }

        public void SetPopupIsOpen(bool isOpen)
        {
            //    if(!Dispatcher->HasShutdownStarted) {
            //        Dispatcher->BeginInvoke(gcnew Action<bool>(this, &WebView::ShowHidePopup), DispatcherPriority::Render, isOpen);
            //    }
        }

        public void SetPopupSizeAndPosition(IntPtr rect)
        {
            //    auto cefRect = (const CefRect&) rect;

            //    _popupX = cefRect.x;
            //    _popupY = cefRect.y;
            //    _popupWidth = cefRect.width;
            //    _popupHeight = cefRect.height;

            //    if(!Dispatcher->HasShutdownStarted) {
            //        Dispatcher->BeginInvoke(DispatcherPriority::Render, _resizePopupDelegate);
            //    }
        }

        //void WebView::SetPopupSizeAndPositionImpl()
        //{
        //    _popup->Width = _popupWidth;
        //    _popup->Height = _popupHeight;

        //    _popup->HorizontalOffset = _popupX;
        //    _popup->VerticalOffset = _popupY;
        //}

        public void ClearBitmap()
        {
            interopBitmap = null;
        }

        public void SetBitmap()
        {
            var bitmap = interopBitmap;

            lock (cefBrowserWrapper.BitmapLock)
            {
                if (bitmap == null)
                {
                    image.Source = null;
                    GC.Collect(1);

                    var stride = cefBrowserWrapper.BitmapWidth * BytesPerPixel;

                    bitmap = (InteropBitmap) Imaging.CreateBitmapSourceFromMemorySection(FileMappingHandle,
                        cefBrowserWrapper.BitmapWidth, cefBrowserWrapper.BitmapHeight, PixelFormats.Bgr32, stride, 0);
                    image.Source = bitmap;
                    interopBitmap = bitmap;
                }

                interopBitmap.Invalidate();
            }
        }

        public void SetPopupBitmap()
        {
            throw new NotImplementedException();
            //if(popupInteropBitmap == null) 
            //{
            //    _popupImage->Source = nullptr;
            //    GC::Collect(1);

            //    int stride = _popupImageWidth * PixelFormats::Bgr32.BitsPerPixel / 8;
            //    bitmap = (InteropBitmap^)Interop::Imaging::CreateBitmapSourceFromMemorySection(
            //        (IntPtr)_popupFileMappingHandle, _popupImageWidth, _popupImageHeight, PixelFormats::Bgr32, stride, 0);
            //    _popupImage->Source = bitmap;
            //    _popupIbitmap = bitmap;
            //}

            //bitmap->Invalidate();
        }

        public void ViewSource()
        {
            cefBrowserWrapper.ViewSource();
        }
    }
}