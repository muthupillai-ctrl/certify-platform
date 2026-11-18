import { Component, OnInit, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { SwUpdate, VersionReadyEvent } from '@angular/service-worker';
import { START_URL } from './core/start-url';

// A new version that's ready this soon after the page opened replaces the
// cached one at once (nothing has been done on the page yet); later ones
// wait for the user (the Reload bar).
const SWITCH_SILENTLY_WITHIN_MS = 15_000;
import { filter } from 'rxjs';
import { Title } from '@angular/platform-browser';
import { RouterOutlet } from '@angular/router';
import { environment } from '../environments/environment';
import { InstallPromptService } from './core/services/install-prompt.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  template: `
    <router-outlet></router-outlet>
    @if (updateReady()) {
      <!-- The offline app (service worker) keeps running the version it
           has until told otherwise; this lets people switch at once. -->
      <div class="fixed top-3 left-1/2 -translate-x-1/2 z-[110] flex items-center gap-3 px-4 py-2 rounded-lg bg-gray-900 text-white text-sm shadow-lg print:hidden"
        role="status">
        A new version of Certyfi is available.
        <button type="button" (click)="reload()" class="px-3 py-1 rounded-md bg-white text-gray-900 font-medium hover:bg-gray-100 cursor-pointer">Reload</button>
      </div>
    }
    @if (environmentLabel(); as label) {
      <!-- Shown on every page of a non-production system (the server's
           App:EnvironmentLabel), so it's never mistaken for production. -->
      <div class="fixed bottom-3 left-1/2 -translate-x-1/2 z-[100] pointer-events-none select-none
                  px-3 py-1 rounded-full bg-orange-600 text-white text-xs font-bold tracking-widest shadow-lg print:hidden"
        role="status">
        {{ label }}
      </div>
    }
  `,
})
export class AppComponent implements OnInit {
  readonly environmentLabel = signal<string | null>(null);
  readonly updateReady = signal(false);

  // InstallPromptService is injected only so it exists from startup and
  // catches the browser's one-time install prompt.
  constructor(_installPrompt: InstallPromptService, private http: HttpClient, private title: Title, private updates: SwUpdate) {}

  reload(): void {
    this.updates.activateUpdate().finally(() => document.location.reload());
  }

  ngOnInit(): void {
    // After a deploy, the service worker first shows the version it cached
    // and downloads the new one in the background. Ready right after the
    // page opened: switch to it straight away, at the address that was
    // opened (the cached app may not know a new page and have redirected).
    // Later: offer the Reload bar. Checked at start and every 30 minutes
    // for tabs left open.
    if (this.updates.isEnabled) {
      const openedAt = Date.now();
      this.updates.versionUpdates
        .pipe(filter((e): e is VersionReadyEvent => e.type === 'VERSION_READY'))
        .subscribe(() => {
          if (Date.now() - openedAt < SWITCH_SILENTLY_WITHIN_MS) {
            this.updates.activateUpdate().finally(() => document.location.replace(START_URL));
          } else {
            this.updateReady.set(true);
          }
        });
      const check = () => this.updates.checkForUpdate().catch(() => undefined);
      check();
      setInterval(check, 30 * 60 * 1000);
    }

    this.http.get<{ environmentLabel: string | null }>(`${environment.apiUrl}/system-info`).subscribe({
      next: (info) => {
        if (!info.environmentLabel) return;
        this.environmentLabel.set(info.environmentLabel);
        this.title.setTitle(`[${info.environmentLabel}] ${this.title.getTitle()}`);
      },
      error: () => {
        // No banner if it can't be checked.
      },
    });
  }
}
