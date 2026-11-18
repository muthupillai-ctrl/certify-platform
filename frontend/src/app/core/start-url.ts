// The address the page was opened at, read when the app's code first loads:
// before the router can redirect it (an older cached app, not knowing a new
// page, sends it to /login). See AppComponent's update handling.
export const START_URL = window.location.pathname + window.location.search + window.location.hash;
