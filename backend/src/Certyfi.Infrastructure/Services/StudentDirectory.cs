using Certyfi.Domain.Entities;
using Certyfi.Infrastructure.MultiTenancy;
using Certyfi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Certyfi.Infrastructure.Services;

// Finds or creates the Student a certificate belongs to. A student is one
// email per institution; a partner app's own student id (StudentNumber) is
// matched first and attached to an email-only student the first time it's
// seen. New students are saved straight away (outside the certificate's own
// transaction), so two requests racing to create the same student both end
// up with the one row the unique indexes allow.
public class StudentDirectory
{
    private readonly TenantDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly StudentSignInIndex _signInIndex;

    public StudentDirectory(TenantDbContext db, ITenantContext tenant, StudentSignInIndex signInIndex)
    {
        _db = db;
        _tenant = tenant;
        _signInIndex = signInIndex;
    }

    // OldEmail: the request changed the student's email (from this one).
    public record Resolution(Student? Student, string? Conflict, string? OldEmail = null);

    // A staff edit, or the details a partner app sent.
    public record StudentEdit(string FullName, string Email, string? Phone, string? StudentNumber);
    // Error: invalid input (400). Conflict: the email or student ID belongs
    // to another student (409). OldEmail: set when the email changed.
    public record UpdateResult(string? Error, string? Conflict, string? OldEmail);

    // For a certificate created by staff or an import: by email only. Null
    // when there's no email to go by.
    public async Task<Student?> ForRecipientAsync(string email, string name, CancellationToken ct)
    {
        var normalized = Normalize(email);
        if (normalized is null) return null;
        return await _db.Students.FirstOrDefaultAsync(s => s.Email == normalized, ct)
            ?? await CreateAsync(new Student { Email = normalized, FullName = name, Source = StudentSource.Certificate }, ct);
    }

    // For a Partner API request. A known student ID with a new email or name
    // updates the student (the partner owns its learners' details), recorded
    // as made by the key (actorId/actorName). Conflict is set (and Student
    // null) when the email belongs to a different student.
    public async Task<Resolution> ForPartnerAsync(string? studentNumber, string email, string name, Guid actorId, string actorName, CancellationToken ct)
    {
        var normalized = Normalize(email)!;
        if (studentNumber is null) return new Resolution(await ForRecipientAsync(normalized, name, ct), null);

        var byNumber = await _db.Students.FirstOrDefaultAsync(s => s.StudentNumber == studentNumber, ct);
        if (byNumber is not null)
        {
            if (byNumber.Email == normalized && byNumber.FullName == name) return new Resolution(byNumber, null);
            var update = await UpdateAsync(byNumber, new StudentEdit(name, normalized, byNumber.Phone, byNumber.StudentNumber), actorId, actorName, ct);
            if (update.Conflict is not null)
            {
                return new Resolution(null, $"studentId '{studentNumber}' can't be given recipientEmail {normalized}: that email belongs to another student.");
            }
            if (update.Error is not null) return new Resolution(null, update.Error);
            return new Resolution(byNumber, null, update.OldEmail);
        }

        var byEmail = await _db.Students.FirstOrDefaultAsync(s => s.Email == normalized, ct);
        if (byEmail is null)
        {
            return new Resolution(await CreateAsync(new Student
            {
                Email = normalized, FullName = name, StudentNumber = studentNumber, Source = StudentSource.PartnerApi,
            }, ct), null);
        }
        if (byEmail.StudentNumber is not null)
        {
            return new Resolution(null, $"recipientEmail already belongs to studentId '{byEmail.StudentNumber}'.");
        }

        Record(byEmail, "Student ID", null, studentNumber, actorId, actorName);
        byEmail.StudentNumber = studentNumber;
        await _db.SaveChangesAsync(ct);
        return new Resolution(byEmail, null);
    }

    // Applies an edit, recording each changed field (StudentChange). Email
    // and student ID must stay unique; the email is the student's sign-in, so
    // they sign in with the new one (same password) from now on. Issued
    // certificates keep the details they were issued with.
    public async Task<UpdateResult> UpdateAsync(Student student, StudentEdit edit, Guid actorId, string actorName, CancellationToken ct)
    {
        var name = edit.FullName?.Trim() ?? "";
        var email = Normalize(edit.Email);
        var phone = string.IsNullOrWhiteSpace(edit.Phone) ? null : edit.Phone.Trim();
        var number = string.IsNullOrWhiteSpace(edit.StudentNumber) ? null : edit.StudentNumber.Trim();
        if (name.Length is 0 or > 200) return new UpdateResult("Name is required (max 200 characters).", null, null);
        if (email is null || email.Length > 320 || !IsEmail(email)) return new UpdateResult("Enter a valid email address.", null, null);
        if (phone is { Length: > 40 }) return new UpdateResult("Phone must be at most 40 characters.", null, null);
        if (number is { Length: > 100 }) return new UpdateResult("Student ID must be at most 100 characters.", null, null);

        if (email != student.Email && await _db.Students.AnyAsync(s => s.Email == email && s.Id != student.Id, ct))
        {
            return new UpdateResult(null, "Another student already has this email.", null);
        }
        if (number is not null && number != student.StudentNumber
            && await _db.Students.AnyAsync(s => s.StudentNumber == number && s.Id != student.Id, ct))
        {
            return new UpdateResult(null, "Another student already has this student ID.", null);
        }

        var oldEmail = student.Email;
        Record(student, "Name", student.FullName, name, actorId, actorName);
        Record(student, "Email", student.Email, email, actorId, actorName);
        Record(student, "Phone", student.Phone, phone, actorId, actorName);
        Record(student, "Student ID", student.StudentNumber, number, actorId, actorName);
        student.FullName = name;
        student.Email = email;
        student.Phone = phone;
        student.StudentNumber = number;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Taken by another request a moment ago.
            _db.ChangeTracker.Clear();
            return new UpdateResult(null, "Another student already has this email or student ID.", null);
        }
        if (oldEmail == email) return new UpdateResult(null, null, null);
        await _signInIndex.AddAsync(_tenant.TenantId, email, ct);
        await _signInIndex.RemoveAsync(_tenant.TenantId, oldEmail, ct);
        return new UpdateResult(null, null, oldEmail);
    }

    private void Record(Student student, string field, string? oldValue, string? newValue, Guid actorId, string actorName)
    {
        if (oldValue == newValue) return;
        _db.StudentChanges.Add(new StudentChange
        {
            StudentId = student.Id, Field = field, OldValue = oldValue, NewValue = newValue,
            ChangedByUserId = actorId, ChangedByName = actorName.Length > 200 ? actorName[..200] : actorName,
        });
    }

    private static bool IsEmail(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 && at == email.LastIndexOf('@') && email.IndexOf('.', at) > at + 1 && !email.EndsWith('.') && !email.Any(char.IsWhiteSpace);
    }

    // A student was deleted: off the email-only sign-in index.
    public Task ForgetSignInAsync(string email, CancellationToken ct) => _signInIndex.RemoveAsync(_tenant.TenantId, email, ct);

    public static string? Normalize(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    private async Task<Student> CreateAsync(Student student, CancellationToken ct)
    {
        student.FullName = student.FullName.Length > 200 ? student.FullName[..200] : student.FullName;
        _db.Students.Add(student);
        try
        {
            await _db.SaveChangesAsync(ct);
            await _signInIndex.AddAsync(_tenant.TenantId, student.Email, ct);
            return student;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another request created the same student first: use theirs.
            _db.Entry(student).State = EntityState.Detached;
            return await _db.Students.FirstAsync(s => s.Email == student.Email, ct);
        }
    }
}
