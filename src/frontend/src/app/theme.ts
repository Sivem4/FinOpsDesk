import { createTheme } from '@mui/material/styles';

export const theme = createTheme({
  palette: {
    mode: 'light',
    primary: { main: '#0f3d4c' },
    background: { default: '#f4f7f8' },
  },
  typography: {
    fontFamily: '"Segoe UI", "Helvetica Neue", sans-serif',
    body1: { fontSize: '0.875rem', lineHeight: 20 / 14 },
    body2: { fontSize: '0.875rem', lineHeight: 20 / 14 },
  },
  components: {
    MuiFormHelperText: {
      styleOverrides: {
        root: ({ theme }) => ({ ...theme.typography.body2 }),
      },
    },
    MuiFormLabel: {
      styleOverrides: { root: { fontSize: '1rem', lineHeight: 1.5 } },
    },
    MuiFormControlLabel: {
      styleOverrides: { label: { fontSize: '1rem', lineHeight: 1.5 } },
    },
    MuiInputBase: {
      styleOverrides: { root: { fontSize: '1rem', lineHeight: 1.5 } },
    },
    MuiTableCell: {
      styleOverrides: {
        root: ({ theme }) => ({ ...theme.typography.body2 }),
        head: { fontWeight: 600 },
      },
    },
    MuiTooltip: {
      styleOverrides: {
        tooltip: ({ theme }) => ({ ...theme.typography.body2 }),
      },
    },
  },
  shape: { borderRadius: 8 },
});
