trait Mark {}
fn keep<T: Mark>(value: T) -> T { value }
fn relay<T>(value: T) -> T { keep(value) }
fn main() {}
