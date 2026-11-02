let cookieAction: Promise<void> = Promise.resolve();

export function enqueueCookieAction(action: () => Promise<void>): Promise<void> {
  const run = cookieAction.then(action, action);
  cookieAction = run.then(() => undefined, () => undefined);
  return run;
}

export function resetCookieActionsForTests(): void {
  cookieAction = Promise.resolve();
}
