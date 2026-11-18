import { Component, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HomeAccessNumber, HomeAccessService } from '../../../core/services/home-access.service';

// Who may open the private home page (the presentation). Only the home page
// is private: verification, logins and the Partner API are open to all.
@Component({
  selector: 'app-home-access',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './home-access.component.html',
})
export class HomeAccessComponent implements OnInit {
  readonly numbers = signal<HomeAccessNumber[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly adding = signal(false);
  number = '';
  label = '';

  constructor(private access: HomeAccessService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.access.list().subscribe({
      next: (list) => {
        this.numbers.set(list);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.error.set('Could not load the list.');
      },
    });
  }

  add(): void {
    this.adding.set(true);
    this.error.set(null);
    this.access.add(this.number, this.label).subscribe({
      next: () => {
        this.adding.set(false);
        this.number = '';
        this.label = '';
        this.load();
      },
      error: (err) => {
        this.adding.set(false);
        this.error.set(err.error?.error ?? 'Could not add the number.');
      },
    });
  }

  remove(n: HomeAccessNumber): void {
    if (!confirm(`Remove ${n.label} (+${n.number})? They'll be asked for a number again on their next visit.`)) return;
    this.access.remove(n.id).subscribe({ next: () => this.load(), error: () => this.error.set('Could not remove the number.') });
  }
}
