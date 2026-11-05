import { AppBar, Box, Button, Container, Stack, Toolbar, Typography } from '@mui/material';
import { NavLink } from 'react-router';
import type { ReactNode } from 'react';

export function AppShell({
  children,
  onLogout,
  onReplace,
  links,
}: {
  children: ReactNode;
  onLogout: () => void;
  onReplace?: () => void;
  links?: readonly { to: string; label: string }[];
}) {
  const navigation = links ?? [
    { to: '/invoices', label: 'Invoices' },
    { to: '/bank-transactions', label: 'Bank transactions' },
    { to: '/imports', label: 'Imports' },
    { to: '/reconciliation', label: 'Reconciliation' },
    { to: '/allocations', label: 'Allocation history' },
    { to: '/review-cases', label: 'Review cases' },
  ];

  return (
    <Box sx={{ minHeight: '100vh', bgcolor: 'background.default' }}>
      <AppBar position="static" color="primary" elevation={0}>
        <Toolbar sx={{ gap: 2, flexWrap: 'wrap' }}>
          <Typography variant="h6" component="div" sx={{ flexGrow: 1 }}>
            FinOpsDesk
          </Typography>
          {navigation.map((link) => (
            <Button key={link.to} component={NavLink} to={link.to} color="inherit">
              {link.label}
            </Button>
          ))}
          {onReplace === undefined ? null : (
            <Button color="inherit" onClick={onReplace}>
              Replace token
            </Button>
          )}
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
