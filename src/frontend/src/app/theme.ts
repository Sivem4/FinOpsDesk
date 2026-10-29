import { createTheme } from '@mui/material/styles';

export const theme = createTheme({
  palette: {
    mode: 'light',
    primary: { main: '#0f3d4c' },
    background: { default: '#f4f7f8' },
  },
  typography: {
    fontFamily: '"Segoe UI", "Helvetica Neue", sans-serif',
  },
  shape: { borderRadius: 8 },
});
