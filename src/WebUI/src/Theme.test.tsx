import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ThemeProvider } from './Theme';

let dark: boolean;
let listeners: Set<() => void>;

function changeSystemTheme(value: boolean) {
  dark = value;
  listeners.forEach(listener => listener());
}

beforeEach(() => {
  dark = false;
  listeners = new Set();
  window.localStorage.clear();
  vi.stubGlobal('matchMedia', vi.fn(() => ({
    get matches() { return dark; },
    addEventListener: (_type: string, listener: () => void) => listeners.add(listener),
    removeEventListener: (_type: string, listener: () => void) => listeners.delete(listener),
  })));
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  window.localStorage.clear();
});

describe('theme selection', () => {
  it('keeps the selected theme after remount without losing current form input', () => {
    const view = render(<ThemeProvider><input aria-label="Draft" /></ThemeProvider>);
    fireEvent.change(screen.getByLabelText('Draft'), { target: { value: 'unfinished work' } });
    fireEvent.change(screen.getByLabelText('Theme'), { target: { value: 'dark' } });
    expect(document.documentElement).toHaveAttribute('data-theme', 'dark');
    expect(screen.getByLabelText('Draft')).toHaveValue('unfinished work');
    expect(window.localStorage.getItem('hic.theme')).toBe('dark');

    view.unmount();
    render(<ThemeProvider><div>Content</div></ThemeProvider>);
    expect(screen.getByLabelText('Theme')).toHaveValue('dark');
    expect(document.documentElement).toHaveAttribute('data-theme', 'dark');
  });

  it('follows operating system changes only when system is selected', () => {
    render(<ThemeProvider><div>Content</div></ThemeProvider>);
    expect(screen.getByLabelText('Theme')).toHaveValue('system');
    expect(document.documentElement).toHaveAttribute('data-theme', 'light');
    changeSystemTheme(true);
    expect(document.documentElement).toHaveAttribute('data-theme', 'dark');

    fireEvent.change(screen.getByLabelText('Theme'), { target: { value: 'light' } });
    changeSystemTheme(true);
    expect(document.documentElement).toHaveAttribute('data-theme', 'light');

    fireEvent.change(screen.getByLabelText('Theme'), { target: { value: 'system' } });
    expect(document.documentElement).toHaveAttribute('data-theme', 'dark');
    changeSystemTheme(false);
    expect(document.documentElement).toHaveAttribute('data-theme', 'light');
  });

  it('restores system mode and applies the current system preference after reload', () => {
    window.localStorage.setItem('hic.theme', 'system');
    dark = true;
    render(<ThemeProvider><div>Content</div></ThemeProvider>);
    expect(screen.getByLabelText('Theme')).toHaveValue('system');
    expect(document.documentElement).toHaveAttribute('data-theme', 'dark');
  });

  it('warns when the browser cannot save the preference', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('denied'); });
    render(<ThemeProvider><div>Content</div></ThemeProvider>);
    fireEvent.change(screen.getByLabelText('Theme'), { target: { value: 'dark' } });
    expect(document.documentElement).toHaveAttribute('data-theme', 'dark');
    expect(screen.getByRole('alert')).toHaveTextContent('could not be saved');
  });
});
