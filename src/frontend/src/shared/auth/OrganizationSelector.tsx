import { Button, Stack, Typography } from '@mui/material';

import type { BrowserOrganization } from '../api/session-store';

export function OrganizationSelector({
  organizations,
  activeOrganization,
  disabled,
  onSelect,
}: {
  organizations: BrowserOrganization[];
  activeOrganization: BrowserOrganization | null;
  disabled: boolean;
  onSelect: (organizationId: string) => void;
}) {
  if (organizations.length === 0) {
    return (
      <Typography>
        Authentication succeeded, but no organization membership is available.
      </Typography>
    );
  }

  return (
    <Stack spacing={1}>
      {activeOrganization === null ? (
        <Typography>Choose an organization. Signing in does not select one.</Typography>
      ) : (
        <Typography>Active organization: {activeOrganization.displayName}</Typography>
      )}
      {organizations.map((organization) => {
        const active = activeOrganization?.organizationId === organization.organizationId;

        if (active) {
          return null;
        }

        const label = activeOrganization === null
          ? `Select ${organization.displayName}`
          : `Switch to ${organization.displayName}`;

        return (
          <Button
            key={organization.organizationId}
            type="button"
            variant="contained"
            disabled={disabled}
            onClick={() => onSelect(organization.organizationId)}
          >
            {label}
          </Button>
        );
      })}
    </Stack>
  );
}
