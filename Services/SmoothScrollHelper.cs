using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ApexUninstaller.Services
{
    public static class SmoothScrollHelper
    {
        private static readonly HashSet<ScrollViewer> _attached = new HashSet<ScrollViewer>();

        public static void Register(ListView listView)
        {
            if (listView == null) return;

            ScrollViewer.SetCanContentScroll(listView, false);

            Action tryAttach = () =>
            {
                var sv = FindScrollViewer(listView);
                if (sv != null)
                {
                    Attach(sv);
                }
            };

            listView.Loaded += (s, e) => tryAttach();
            listView.IsVisibleChanged += (s, e) =>
            {
                if (listView.IsVisible)
                {
                    listView.Dispatcher.BeginInvoke(new Action(tryAttach), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            };

            tryAttach();
        }

        public static void Attach(ScrollViewer sv)
        {
            if (sv == null) return;
            if (_attached.Contains(sv)) return;
            _attached.Add(sv);

            sv.CanContentScroll = false;

            double targetOffset = sv.VerticalOffset;
            double startOffset = sv.VerticalOffset;
            DateTime animStartTime = DateTime.UtcNow;
            double durationMs = 260.0;
            bool isAnimating = false;

            EventHandler renderHandler = null;
            renderHandler = (s, e) =>
            {
                if (!isAnimating) return;

                double elapsed = (DateTime.UtcNow - animStartTime).TotalMilliseconds;
                double t = Math.Min(1.0, elapsed / durationMs);

                // Smooth Cubic Ease-Out: f(t) = 1 - (1 - t)^3
                double progress = 1.0 - Math.Pow(1.0 - t, 3.0);
                double newOffset = startOffset + (targetOffset - startOffset) * progress;

                sv.ScrollToVerticalOffset(newOffset);

                if (t >= 1.0 || Math.Abs(newOffset - targetOffset) < 0.25)
                {
                    sv.ScrollToVerticalOffset(targetOffset);
                    isAnimating = false;
                    CompositionTarget.Rendering -= renderHandler;
                }
            };

            sv.PreviewMouseWheel += (s, e) =>
            {
                e.Handled = true;

                double maxScroll = sv.ScrollableHeight;
                if (maxScroll <= 0) return;

                // 1 notch = 120 delta -> 115px smooth travel (~3 rows)
                double step = -(e.Delta / 120.0) * 115.0;

                // Fluid momentum chaining when user spins wheel rapidly in the same direction
                if (isAnimating && Math.Sign(step) == Math.Sign(targetOffset - startOffset))
                {
                    targetOffset = Math.Max(0.0, Math.Min(maxScroll, targetOffset + step));
                    startOffset = sv.VerticalOffset;
                    animStartTime = DateTime.UtcNow;
                    durationMs = 300.0;
                }
                else
                {
                    startOffset = sv.VerticalOffset;
                    targetOffset = Math.Max(0.0, Math.Min(maxScroll, startOffset + step));
                    animStartTime = DateTime.UtcNow;
                    durationMs = 260.0;
                }

                if (!isAnimating)
                {
                    isAnimating = true;
                    CompositionTarget.Rendering += renderHandler;
                }
            };

            sv.ScrollChanged += (s, e) =>
            {
                if (!isAnimating)
                {
                    targetOffset = sv.VerticalOffset;
                    startOffset = sv.VerticalOffset;
                }
            };
        }

        private static ScrollViewer FindScrollViewer(DependencyObject d)
        {
            if (d == null) return null;
            if (d is ScrollViewer) return (ScrollViewer)d;
            int count = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(d, i);
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }
    }
}
