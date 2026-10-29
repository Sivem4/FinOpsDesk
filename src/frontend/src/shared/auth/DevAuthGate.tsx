import { zodResolver } from '@hookform/resolvers/zod';
import { Alert, Button, Container, Stack, TextField, Typography } from '@mui/material';
import { useEffect, type ReactNode } from 'react';
import { useForm } from 'react-hook-form';
import { z } from 'zod';

import { endSession, setAccessToken, useSession } from './session';
import { readUntrustedExpiry } from './token';
import { AppShell } from '../ui/AppShell';

const tokenSchema = z.object({
  token: z
    .string()
    .trim()
    .min(1, 'Enter a development access token.')
    .superRefine((token, context) => {
      const expiresAt = readUntrustedExpiry(token);

      if (expiresAt === null) {
        context.addIssue({
          code: 'custom',
          message: 'Enter a JWT that includes an expiration claim.',
        });
        return;
      }

      if (expiresAt <= Math.floor(Date.now() / 1000)) {
        context.addIssue({
          code: 'custom',
          message: 'This token is already expired.',
        });
      }
    }),
});

type TokenFormValues = z.infer<typeof tokenSchema>;

export default function DevAuthGate({ children }: { children: ReactNode }) {
  const session = useSession();

  useEffect(() => {
    if (session.expiresAt === null) {
      return;
    }

    const delay = session.expiresAt * 1000 - Date.now();

    if (delay <= 0) {
      endSession('expired');
      return;
    }

    const timer = window.setTimeout(() => endSession('expired'), delay);
    return () => window.clearTimeout(timer);
  }, [session.epoch, session.expiresAt]);

  if (session.token === null) {
    return (
      <TokenEntry
        reason={session.reason}
        onSubmit={(token, expiresAt) => {
          setAccessToken(token, expiresAt);
        }}
      />
    );
  }

  return (
    <AppShell onLogout={() => endSession(null)} onReplace={() => endSession(null)}>
      {children}
    </AppShell>
  );
}

function TokenEntry({
  reason,
  onSubmit,
}: {
  reason: ReturnType<typeof useSession>['reason'];
  onSubmit: (token: string, expiresAt: number) => void;
}) {
  const {
    register,
    handleSubmit,
    formState: { errors },
  } = useForm<TokenFormValues>({
    resolver: zodResolver(tokenSchema),
    defaultValues: { token: '' },
  });

  return (
    <Container maxWidth="sm" sx={{ py: { xs: 4, sm: 8 } }}>
      <Stack spacing={2} component="form" onSubmit={handleSubmit((values) => {
        const expiresAt = readUntrustedExpiry(values.token);

        if (expiresAt === null) {
          return;
        }

        onSubmit(values.token.trim(), expiresAt);
      })}>
        <Typography variant="h4" component="h1">
          FinOpsDesk
        </Typography>
        <Alert severity="info">
          Development access token. This screen is not a login. The token stays in memory and is
          discarded when the page reloads. Production browser sign-in is part of Phase 7.
        </Alert>
        {reason === 'expired' ? <Alert severity="warning">The development token expired.</Alert> : null}
        {reason === 'unauthorized' ? (
          <Alert severity="warning">The access token was rejected.</Alert>
        ) : null}
        {reason === 'forbidden' ? (
          <Alert severity="warning">The token does not contain a valid organization.</Alert>
        ) : null}
        <TextField
          {...register('token')}
          label="Development access token"
          type="password"
          autoComplete="off"
          error={errors.token !== undefined}
          helperText={errors.token?.message ?? 'Paste the token printed by scripts/dev-token.ps1.'}
          fullWidth
        />
        <Button type="submit" variant="contained">
          Use token
        </Button>
      </Stack>
    </Container>
  );
}
