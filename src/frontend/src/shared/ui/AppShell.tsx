import { AppBar, Box, Button, Container, Stack, Toolbar, Typography } from '@mui/material';
import { NavLink } from 'react-router';
import type { ReactNode } from 'react';

export function AppShell({
  children,
  onLogout,
  onReplace,
}: {
  children: ReactNode;
  onLogout: () => void;
  onReplace: () => void;
}) {
  return (
    <Box sx={{ minHeight: '100vh', bgcolor: 'background.default' }}>
      <AppBar position="static" color="primary" elevation={0}>
        <Toolbar sx={{ gap: 2, flexWrap: 'wrap' }}>
          <Typography variant="h6" component="div" sx={{ flexGrow: 1 }}>
            FinOpsDesk
          </Typography>
          <Button component={NavLink} to="/invoices" color="inherit">
            Invoices
          </Button>
          <Button component={NavLink} to="/imports" color="inherit">
            Imports
          </Button>
          <Button component={NavLink} to="/reconciliation" color="inherit">
            Reconciliation
          </Button>
          <Button color="inherit" onClick={onReplace}>
            Replace token
          </Button>
          <Button color="inherit" onClick={onLogout}>
            Log out
          </Button>
        </Toolbar>
      </AppBar>
      <Container maxWidth="lg" sx={{ py: { xs: 2, sm: 3 } }}>
        <Stack spacing={2}>{children}</Stack>
      </Container>
    </Box>
  );
}
