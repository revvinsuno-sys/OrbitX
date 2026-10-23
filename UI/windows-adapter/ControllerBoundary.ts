import { useEffect, useRef } from 'react';

// Used by Orbit's existing poller. Focus changes never create another input loop.
export class FocusedControllerInput {
  private waitingForRelease = true;
  reset() { this.waitingForRelease = true; }
  ready(pad: Gamepad | null | undefined) {
    if (document.hidden || !document.hasFocus()) { this.reset(); return false; }
    if (!pad) { this.reset(); return false; }
    if (this.waitingForRelease) {
      if (!pad.buttons.some(button => button.pressed || button.value > 0.25) &&
          pad.axes.every(axis => Math.abs(axis) <= 0.25)) this.waitingForRelease = false;
      return false;
    }
    return true;
  }
}

export function dismissWindowsLayer() {
  const close = document.querySelector<HTMLButtonElement>('[role="alertdialog"] [data-dialog-back], [role="dialog"] [data-dialog-back], [role="dialog"] .close');
  if (close) { close.click(); return true; }
  return !document.dispatchEvent(new Event('windows-back', { cancelable: true }));
}

// The shell owns page changes; this records only pages actually visited.
export function usePageHistory<T extends string>(page: T) {
  const history = useRef<T[]>([page]);
  useEffect(() => {
    if (history.current[history.current.length - 1] !== page) history.current.push(page);
  }, [page]);
  return () => {
    if (history.current.length < 2) return null;
    history.current.pop();
    return history.current[history.current.length - 1];
  };
}
