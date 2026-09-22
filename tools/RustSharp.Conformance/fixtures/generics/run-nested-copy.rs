fn identity<T>(value: T) -> T { value }
fn main() {
    let original = ((42, true), 7);
    let copied = identity(original);
    let inner = identity(copied.0);
    println!("{}", inner.0);
    println!("{}", original.0.1);
    println!("{}", copied.1);
}
