import { Alert, Container, Typography } from '@mui/material';

export function ProductionAuthNotice() {
  return (
    <Container maxWidth="sm" sx={{ py: 8 }}>
      <Typography variant="h4" component="h1" gutterBottom>
        FinOpsDesk
      </Typography>
      <Alert severity="info">
        Browser authentication is not configured. Production browser sign-in is part of Phase 7.
      </Alert>
    </Container>
  );
}
