import { CssBaseline, ThemeProvider } from '@mui/material';
import { QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter } from 'react-router';

import { queryClient } from '../shared/api/query-client';
import { AuthGate } from '../shared/auth/AuthGate';
import { SessionProvider } from '../shared/auth/session-context';
import { AppRouter } from './router';
import { theme } from './theme';

export function App() {
  return (
    <ThemeProvider theme={theme}>
      <CssBaseline />
      <QueryClientProvider client={queryClient}>
        <SessionProvider>
          <BrowserRouter>
            <AuthGate>
              <AppRouter />
            </AuthGate>
          </BrowserRouter>
        </SessionProvider>
      </QueryClientProvider>
    </ThemeProvider>
  );
}
