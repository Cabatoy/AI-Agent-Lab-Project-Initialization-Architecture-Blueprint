import { createRoot } from 'react-dom/client';
import App from './App';
import { ThemeProvider } from './Theme';
import './styles.css';
import './theme.css';

createRoot(document.getElementById('root')!).render(<ThemeProvider><App /></ThemeProvider>);
