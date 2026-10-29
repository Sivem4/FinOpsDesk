import { Alert } from '@mui/material';
import { Navigate, Route, Routes } from 'react-router';

import { ImportWorkspacePage } from '../features/imports/ImportWorkspacePage';
import { InvoiceRegisterPage } from '../features/invoices/InvoiceRegisterPage';

export function AppRouter() {
  return (
    <Routes>
      <Route path="/" element={<Navigate to="/invoices" replace />} />
      <Route path="/invoices" element={<InvoiceRegisterPage />} />
      <Route path="/imports" element={<ImportWorkspacePage />} />
      <Route
        path="*"
        element={<Alert severity="warning">That page is not available.</Alert>}
      />
    </Routes>
  );
}
