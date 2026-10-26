using Intoner.Services.Input;

namespace Intoner.Services
{
    internal sealed class IntonerPluginRuntime
    {
        private readonly IKeyboardInputService      _keyboardInput;
        private readonly IViewportMouseInputService _viewportMouseInput;

        public IntonerPluginRuntime(
            IKeyboardInputService keyboardInput,
            IViewportMouseInputService viewportMouseInput)
        {
            _keyboardInput      = keyboardInput;
            _viewportMouseInput = viewportMouseInput;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _keyboardInput.StartAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await _viewportMouseInput.StartAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
