import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/services/auth.service';

// The Certyfi presentation (a static copy, see
// docs/presentation/publish-to-app.py), private behind the mobile-number
// gate (/access), with the same ways in as the landing page.
@Component({
  selector: 'app-presentation',
  standalone: true,
  imports: [RouterLink],
  templateUrl: './presentation.component.html',
})
export class PresentationComponent {
  constructor(readonly auth: AuthService) {}
}
