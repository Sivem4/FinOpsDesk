import { QueryCache, QueryClient } from '@tanstack/react-query';

import { StaleSessionError } from './errors';

export const queryClient = new QueryClient({
  queryCache: new QueryCache({
    onError: (error, query) => {
      if (error instanceof StaleSessionError) {
        queryClient.removeQueries({ queryKey: query.queryKey });
      }
    },
  }),
  defaultOptions: {
    queries: {
      retry: false,
      refetchOnWindowFocus: false,
    },
  },
});
