struct Container<T> { value: T }
fn preserve<T>(value: Container<T>) -> Container<T> { value }
fn main() {
    let number = preserve(Container::<i32> { value: 42 });
    let flag = preserve(Container { value: true });
    println!("{}", number.value);
    println!("{}", flag.value);
}
