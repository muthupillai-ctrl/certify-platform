import { Injectable, signal } from '@angular/core';

// "Install as an app" for the verify pages. Browsers fire
// beforeinstallprompt once, early, and only when the app is installable
// (production build, HTTPS, service worker active) — so this service is
// created at app start (AppComponent) to catch it. Safari has no prompt;
// the look-up page explains Add to Home Screen instead.
interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
}

@Injectable({ providedIn: 'root' })
export class InstallPromptService {
  readonly available = signal(false);
  readonly installed = signal(window.matchMedia?.('(display-mode: standalone)').matches ?? false);
  private deferred: BeforeInstallPromptEvent | null = null;

  constructor() {
    window.addEventListener('beforeinstallprompt', (event) => {
      event.preventDefault();
      this.deferred = event as BeforeInstallPromptEvent;
      this.available.set(true);
    });
    window.addEventListener('appinstalled', () => {
      this.installed.set(true);
      this.available.set(false);
    });
  }

  async install(): Promise<void> {
    if (!this.deferred) return;
    await this.deferred.prompt();
    await this.deferred.userChoice;
    this.deferred = null;
    this.available.set(false);
  }
}
