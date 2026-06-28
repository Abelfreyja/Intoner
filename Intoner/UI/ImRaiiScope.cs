using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace Intoner.UI;

internal static class ImRaiiScope
{
    public static WindowScope Window(string name, ImGuiWindowFlags flags)
        => new(name, flags);

    public static PopupScope Popup(string id, ImGuiWindowFlags flags)
        => new(id, flags);

    public static TableScope Table(string id, int columns, ImGuiTableFlags flags, Vector2 cellPadding)
        => new(id, columns, flags, cellPadding);

    public static IDisposable TextWrapPos(float wrapPos = 0f)
    {
        ImGui.PushTextWrapPos(wrapPos);
        return new CallbackDisposable(ImGui.PopTextWrapPos);
    }

    public ref struct WindowScope
    {
        private bool _disposed;

        public WindowScope(string name, ImGuiWindowFlags flags)
        {
            Success = ImGui.Begin(name, flags);
        }

        public bool Success { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            ImGui.End();
            _disposed = true;
        }

        public static implicit operator bool(WindowScope value)
            => value.Success;

        public static bool operator true(WindowScope value)
            => value.Success;

        public static bool operator false(WindowScope value)
            => !value.Success;

        public static bool operator !(WindowScope value)
            => !value.Success;
    }

    public ref struct PopupScope
    {
        private bool _disposed;

        public PopupScope(string id, ImGuiWindowFlags flags)
        {
            Success = ImGui.BeginPopup(id, flags);
        }

        public bool Success { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (Success)
            {
                ImGui.EndPopup();
            }

            _disposed = true;
        }

        public static implicit operator bool(PopupScope value)
            => value.Success;

        public static bool operator true(PopupScope value)
            => value.Success;

        public static bool operator false(PopupScope value)
            => !value.Success;

        public static bool operator !(PopupScope value)
            => !value.Success;
    }

    public ref struct TableScope
    {
        private bool _disposed;

        public TableScope(string id, int columns, ImGuiTableFlags flags, Vector2 cellPadding)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, cellPadding);
            Success = ImGui.BeginTable(id, columns, flags);
        }

        public bool Success { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (Success)
            {
                ImGui.EndTable();
            }

            ImGui.PopStyleVar();
            _disposed = true;
        }

        public static implicit operator bool(TableScope value)
            => value.Success;

        public static bool operator true(TableScope value)
            => value.Success;

        public static bool operator false(TableScope value)
            => !value.Success;

        public static bool operator !(TableScope value)
            => !value.Success;
    }

    private sealed class CallbackDisposable : IDisposable
    {
        private readonly Action _disposeAction;
        private bool _disposed;

        public CallbackDisposable(Action disposeAction)
        {
            _disposeAction = disposeAction;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposeAction();
            _disposed = true;
        }
    }
}
