import { Alert } from '@mui/material';
import type { ReactNode } from 'react';
import { Navigate, Route, Routes } from 'react-router';

import { ReadOnlyAllocationHistoryPage } from '../features/allocations/ReadOnlyAllocationHistoryPage';
import { BankTransactionRegisterPage } from '../features/bank-transactions/BankTransactionRegisterPage';
import { InvoiceRegisterPage } from '../features/invoices/InvoiceRegisterPage';
import { ReadOnlyReconciliationPage } from '../features/reconciliation/ReadOnlyReconciliationPage';
import { ReviewCasePage } from '../features/review-cases/ReviewCasePage';
import { firstCookieRoute } from '../shared/auth/cookie-navigation';
import { useSession } from '../shared/auth/session';

export function CookieFinancialRouter() {
  const session = useSession();
  const home = firstCookieRoute(session.capabilities);

  if (session.authorization !== 'verified') {
    return <Alert severity="info">Financial records stay hidden until authorization is verified.</Alert>;
  }

  if (home === null) {
    return (
      <Alert severity="info">
        This organization has no financial screens available in this browser. Financial access stays closed here.
      </Alert>
    );
  }

  return (
    <Routes>
      <Route path="/" element={<Navigate to={home} replace />} />
      <Route
        path="/invoices"
        element={(
          <CapabilityRoute capability="invoices.read">
            <InvoiceRegisterPage showImportLink={false} />
          </CapabilityRoute>
        )}
      />
      <Route
        path="/bank-transactions"
        element={(
          <CapabilityRoute capability="bank-transactions.read">
            <BankTransactionRegisterPage showImportLink={false} />
          </CapabilityRoute>
        )}
      />
      <Route
        path="/reconciliation"
        element={(
          <CapabilityRoute capability="reconciliation.read">
            <ReadOnlyReconciliationPage />
          </CapabilityRoute>
        )}
      />
      <Route
        path="/allocations"
        element={(
          <CapabilityRoute capability="allocations.read">
            <ReadOnlyAllocationHistoryPage />
          </CapabilityRoute>
        )}
      />
      <Route
        path="/review-cases"
        element={(
          <CapabilityRoute capability="review-cases.read">
            <ReviewCasePage />
          </CapabilityRoute>
        )}
      />
      <Route path="*" element={<Alert severity="warning">That page is not available.</Alert>} />
    </Routes>
  );
}

function CapabilityRoute({
  capability,
  children,
}: {
  capability: string;
  children: ReactNode;
}) {
  const session = useSession();

  if (session.authorization !== 'verified' || !session.capabilities.includes(capability)) {
    return <Alert severity="warning">That page is not available.</Alert>;
  }

  return children;
}
