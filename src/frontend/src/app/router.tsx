import { Alert } from '@mui/material';
import { Navigate, Route, Routes } from 'react-router';

import { AllocationHistoryPage } from '../features/allocations/AllocationHistoryPage';
import { BankTransactionRegisterPage } from '../features/bank-transactions/BankTransactionRegisterPage';
import { ImportWorkspacePage } from '../features/imports/ImportWorkspacePage';
import { InvoiceRegisterPage } from '../features/invoices/InvoiceRegisterPage';
import { ReconciliationQueuePage } from '../features/reconciliation/ReconciliationQueuePage';
import { ReviewCasePage } from '../features/review-cases/ReviewCasePage';

export function AppRouter() {
  return (
    <Routes>
      <Route path="/" element={<Navigate to="/invoices" replace />} />
      <Route path="/invoices" element={<InvoiceRegisterPage />} />
      <Route path="/bank-transactions" element={<BankTransactionRegisterPage />} />
      <Route path="/imports" element={<ImportWorkspacePage />} />
      <Route path="/reconciliation" element={<ReconciliationQueuePage />} />
      <Route path="/allocations" element={<AllocationHistoryPage />} />
      <Route path="/review-cases" element={<ReviewCasePage />} />
      <Route
        path="*"
        element={<Alert severity="warning">That page is not available.</Alert>}
      />
    </Routes>
  );
}
