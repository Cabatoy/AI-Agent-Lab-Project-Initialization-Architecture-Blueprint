import { useLayoutEffect, useState, type ReactNode } from 'react';

type Theme = 'system' | 'light' | 'dark';
const storageKey = 'hic.theme';

function isTheme(value: string | null): value is Theme {
  return value === 'system' || value === 'light' || value === 'dark';
}

function readTheme(): { value: Theme; storageError: boolean } {
  try {
    const saved = window.localStorage.getItem(storageKey);
    return { value: isTheme(saved) ? saved : 'system', storageError: false };
  } catch {
    return { value: 'system', storageError: true };
  }
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [initial] = useState(readTheme);
  const [theme, setTheme] = useState<Theme>(initial.value);
  const [storageError, setStorageError] = useState(initial.storageError);

  useLayoutEffect(() => {
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    const apply = () => {
      document.documentElement.dataset.theme = theme === 'system'
        ? (media.matches ? 'dark' : 'light')
        : theme;
    };
    apply();
    if (theme === 'system') media.addEventListener('change', apply);
    return () => {
      if (theme === 'system') media.removeEventListener('change', apply);
      delete document.documentElement.dataset.theme;
    };
  }, [theme]);

  function chooseTheme(value: Theme) {
    setTheme(value);
    try {
      window.localStorage.setItem(storageKey, value);
      setStorageError(false);
    } catch {
      setStorageError(true);
    }
  }

  return <>
    <div className="theme-bar">
      <label htmlFor="theme-selection">Theme</label>
      <select id="theme-selection" value={theme} onChange={event => {
        if (isTheme(event.target.value)) chooseTheme(event.target.value);
      }}>
        <option value="system">System</option>
        <option value="light">Light</option>
        <option value="dark">Dark</option>
      </select>
      {storageError && <span role="alert">Theme preference could not be saved in this browser.</span>}
    </div>
    {children}
  </>;
}
