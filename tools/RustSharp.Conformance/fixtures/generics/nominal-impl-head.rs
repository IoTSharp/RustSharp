struct Container<T> { value: T }
trait Mark {}
impl<T> Mark for Container<T> {}
fn relay<T>(value: Container<T>) -> Container<T> { value }
fn main() {}
